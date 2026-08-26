using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;

namespace Warehouse.Wms.IntegrationTests.Web;

public sealed class FrontendProxyIntegrationTests
{
    [Fact]
    public async Task Proxy_preserves_get_query_and_request_headers()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/echo?sku=A%2FB&limit=2");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        request.Headers.Add("X-Correlation-ID", "corr-123");

        using var response = await fixture.Web.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var capture = await fixture.Upstream.WaitForRequestAsync("/api/echo");
        Assert.Equal("A/B", capture.Query["sku"]);
        Assert.Equal("2", capture.Query["limit"]);
        Assert.Equal("Bearer test-token", capture.Headers["Authorization"]);
        Assert.Equal("application/json", capture.Headers["Accept"]);
        Assert.Equal("corr-123", capture.Headers["X-Correlation-ID"]);
        Assert.Matches("^00-4bf92f3577b34da6a3ce929d0e0e4736-[0-9a-f]{16}-01$", capture.Headers["traceparent"]);
    }

    [Fact]
    public async Task Proxy_preserves_json_post_body_and_does_not_retry_non_idempotent_request()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/commands");
        request.Content = new StringContent("{\"id\":\"cmd-1\"}", Encoding.UTF8, "application/json");

        using var response = await fixture.Web.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, fixture.Upstream.Count("/api/commands"));
        var capture = await fixture.Upstream.WaitForRequestAsync("/api/commands");
        Assert.Equal("application/json; charset=utf-8", capture.Headers["Content-Type"]);
        Assert.Equal("{\"id\":\"cmd-1\"}", capture.Body);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Proxy_does_not_retry_non_idempotent_methods(string method)
    {
        await using var fixture = await ProxyFixture.StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/commands") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

        using var response = await fixture.Web.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, fixture.Upstream.Count("/api/commands"));
    }

    [Fact]
    public async Task Proxy_forwards_multipart_file_name_and_bytes()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[] { 0x50, 0x4B, 0x03, 0x04 }), "file", "inbound.xlsx");

        using var response = await fixture.Web.PostAsync("/api/imports", content);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var capture = await fixture.Upstream.WaitForRequestAsync("/api/imports");
        Assert.Contains("multipart/form-data", capture.Headers["Content-Type"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inbound.xlsx", capture.Body, StringComparison.Ordinal);
        Assert.Contains("PK", capture.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proxy_preserves_download_bytes_content_type_and_disposition()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        using var response = await fixture.Web.GetAsync("/api/reports/errors");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment; filename=errors.xlsx", response.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal(new byte[] { 0x58, 0x4C, 0x53, 0x58 }, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Proxy_cancellation_propagates_to_upstream_request()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Web.GetAsync("/api/cancel", cancellation.Token));
        Assert.True(await fixture.Upstream.WaitForCancellationAsync());
    }

    [Fact]
    public async Task Web_and_api_health_checks_are_layered_and_api_404_does_not_fall_back_to_spa()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        using var webHealth = await fixture.Web.GetAsync("/health/web/live");
        Assert.Equal(HttpStatusCode.OK, webHealth.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Web.GetAsync("/health/api/live")).StatusCode);

        using var api404 = await fixture.Web.GetAsync("/api/missing");
        Assert.Equal(HttpStatusCode.NotFound, api404.StatusCode);
        Assert.DoesNotContain("<!doctype html", await api404.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Web.GetAsync("/index.html")).StatusCode);
        Assert.Contains("立库工作台", await (await fixture.Web.GetAsync("/workbench/inbound")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopped_upstream_returns_problem_502_while_web_health_stays_live()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        await fixture.Upstream.StopAsync();
        var probe = await TcpProbe.TryConnectAsync(fixture.Upstream.BaseAddress);
        Assert.False(probe.Connected, probe.ToString());
        Assert.Equal(SocketError.ConnectionRefused, probe.Error);

        using var response = await fixture.Web.GetAsync("/api/echo");
        Assert.True(response.StatusCode == HttpStatusCode.BadGateway, $"TCP probe: {probe}{Environment.NewLine}{fixture.WebLogs}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Web.GetAsync("/health/web/live")).StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, (await fixture.Web.GetAsync("/health/api/live")).StatusCode);
    }

    [Fact]
    public async Task Slow_upstream_returns_problem_504()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        using var response = await fixture.Web.GetAsync("/api/slow");

        Assert.True(response.StatusCode == HttpStatusCode.GatewayTimeout, fixture.WebLogs);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private sealed class ProxyFixture : IAsyncDisposable
    {
        private readonly WebProcess _web;
        private ProxyFixture(StubApiServer upstream, WebProcess web) { Upstream = upstream; _web = web; Web = new HttpClient { BaseAddress = web.BaseAddress, Timeout = TimeSpan.FromSeconds(10) }; }
        public StubApiServer Upstream { get; }
        public HttpClient Web { get; }
        public string WebLogs => _web.Logs;

        public static async Task<ProxyFixture> StartAsync()
        {
            var upstream = await StubApiServer.StartAsync();
            try { return new ProxyFixture(upstream, await WebProcess.StartAsync(upstream.BaseAddress)); }
            catch { await upstream.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync() { Web.Dispose(); await _web.DisposeAsync(); await Upstream.DisposeAsync(); }
    }

    private sealed class WebProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _logs;
        private WebProcess(Process process, Uri baseAddress, ConcurrentQueue<string> logs) { _process = process; BaseAddress = baseAddress; _logs = logs; }
        public Uri BaseAddress { get; }
        public string Logs => string.Join(Environment.NewLine, _logs);

        public static async Task<WebProcess> StartAsync(Uri upstream)
        {
            var root = FindRepositoryRoot();
            var dll = Path.Combine(root, "src", "Warehouse.Wms.Web", "bin", "Debug", "net8.0", "Warehouse.Wms.Web.dll");
            var contentRoot = Path.Combine(root, "src", "Warehouse.Wms.Web");
            var process = new Process { StartInfo = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls http://127.0.0.1:0 --contentRoot \"{contentRoot}\"") { WorkingDirectory = contentRoot, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
            process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            process.StartInfo.Environment["ASPNETCORE_CONTENTROOT"] = contentRoot;
            process.StartInfo.Environment["ApiProxy__UpstreamBaseUrl"] = upstream.AbsoluteUri;
            process.StartInfo.Environment["ApiProxy__RequestTimeoutSeconds"] = "4";
            process.StartInfo.Environment["Logging__LogLevel__Yarp.ReverseProxy"] = "Trace";
            var logs = new ConcurrentQueue<string>();
            var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is null) return;
                logs.Enqueue(args.Data);
                var marker = args.Data.IndexOf("Now listening on:", StringComparison.OrdinalIgnoreCase);
                if (marker >= 0)
                {
                    var addressStart = args.Data.IndexOf("http://", marker, StringComparison.OrdinalIgnoreCase);
                    if (addressStart >= 0) listening.TrySetResult(new Uri(args.Data[addressStart..].Trim()));
                }
            };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) logs.Enqueue(args.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var completed = await Task.WhenAny(listening.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            if (completed == listening.Task) return new WebProcess(process, await listening.Task, logs);
            process.Kill(true);
            throw new TimeoutException($"Web Kestrel did not report a dynamic listening address: {string.Join(Environment.NewLine, logs)}");
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) { _process.Kill(true); await _process.WaitForExitAsync(); }
            _process.Dispose();
        }
    }

    private sealed class StubApiServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<Capture> _requests = new();
        private readonly TaskCompletionSource<bool> _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private StubApiServer(WebApplication app) { _app = app; }
        public Uri BaseAddress { get; private set; } = null!;

        public static async Task<StubApiServer> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var server = new StubApiServer(app);
            app.Map("/api/{**path}", async context => await server.HandleAsync(context));
            app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
            await app.StartAsync();
            var address = new Uri(app.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
            server.BaseAddress = address;
            return server;
        }

        private async Task HandleAsync(HttpContext context)
        {
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted);
            var capture = new Capture(context.Request.Path, context.Request.Query.ToDictionary(item => item.Key, item => item.Value.ToString()), context.Request.Headers.ToDictionary(item => item.Key, item => item.Value.ToString()), body);
            _requests.Enqueue(capture);
            switch (context.Request.Path)
            {
                case "/api/echo": await Results.Ok(new { ok = true }).ExecuteAsync(context); break;
                case "/api/commands": context.Response.StatusCode = 201; break;
                case "/api/imports": context.Response.StatusCode = 202; break;
                case "/api/reports/errors": context.Response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"; context.Response.Headers.ContentDisposition = "attachment; filename=errors.xlsx"; await context.Response.Body.WriteAsync(new byte[] { 0x58, 0x4C, 0x53, 0x58 }); break;
                case "/api/cancel": try { await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted); } catch (OperationCanceledException) { _cancelled.TrySetResult(true); } break;
                case "/api/slow": await Task.Delay(TimeSpan.FromSeconds(7)); await Results.Ok().ExecuteAsync(context); break;
                default: context.Response.StatusCode = 404; break;
            }
        }

        public int Count(string path) => _requests.Count(item => item.Path == path);
        public async Task<Capture> WaitForRequestAsync(string path) { for (var i = 0; i < 50; i++) { var item = _requests.FirstOrDefault(request => request.Path == path); if (item is not null) return item; await Task.Delay(20); } throw new TimeoutException($"No request for {path}."); }
        public async Task<bool> WaitForCancellationAsync() { await Task.WhenAny(_cancelled.Task, Task.Delay(3000)); return _cancelled.Task.IsCompletedSuccessfully; }
        public async Task StopAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
        public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
    }

    private sealed record Capture(string Path, IReadOnlyDictionary<string, string> Query, IReadOnlyDictionary<string, string> Headers, string Body);

    private sealed record TcpProbe(bool Connected, SocketError? Error, TimeSpan Elapsed)
    {
        public static async Task<TcpProbe> TryConnectAsync(Uri address)
        {
            var started = Stopwatch.StartNew();
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(address.Host, address.Port);
                return new TcpProbe(true, null, started.Elapsed);
            }
            catch (SocketException exception)
            {
                return new TcpProbe(false, exception.SocketErrorCode, started.Elapsed);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Warehouse.Wms.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
