extern alias warehouseWeb;

using System.Text.RegularExpressions;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using WebProxySettings = warehouseWeb::Warehouse.Wms.Web.ApiProxySettings;

namespace Warehouse.Wms.IntegrationTests.Web;

public sealed class FrontendProxyRoutingTests
{
    [Fact]
    public void Web_project_pins_yarp_and_declares_only_the_trusted_proxy_routes()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "src", "Warehouse.Wms.Web", "Warehouse.Wms.Web.csproj"));
        var program = File.ReadAllText(Path.Combine(root, "src", "Warehouse.Wms.Web", "Program.cs"));

        Assert.Contains("Yarp.ReverseProxy\" Version=\"2.2.0", project, StringComparison.Ordinal);
        Assert.Contains("/api/{**catch-all}", program, StringComparison.Ordinal);
        Assert.Contains("/health/api/{**catch-all}", program, StringComparison.Ordinal);
        Assert.Contains("/health/web/live", program, StringComparison.Ordinal);
        Assert.Contains("application/problem+json", program, StringComparison.Ordinal);
        Assert.DoesNotContain("Match = new RouteMatch { Path = \"/health/{**catch-all}\"", program, StringComparison.Ordinal);
        Assert.True(
            program.IndexOf("MapReverseProxy", StringComparison.Ordinal) < program.IndexOf("MapFallbackToFile", StringComparison.Ordinal),
            "Proxy routes must be mapped before the SPA fallback.");
    }

    [Fact]
    public void Proxy_configuration_is_validated_at_startup_and_cannot_be_controlled_by_the_browser()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "Warehouse.Wms.Web", "Program.cs"));
        var developmentSettings = File.ReadAllText(Path.Combine(root, "src", "Warehouse.Wms.Web", "appsettings.Development.json"));

        Assert.Contains("ApiProxy:UpstreamBaseUrl", program, StringComparison.Ordinal);
        Assert.Contains("AllowLoopbackUpstream", program, StringComparison.Ordinal);
        Assert.Contains("Uri.UriSchemeHttp", program, StringComparison.Ordinal);
        Assert.Contains("Uri.UriSchemeHttps", program, StringComparison.Ordinal);
        Assert.Contains("IsProduction", program, StringComparison.Ordinal);
        Assert.Contains("localhost:5054", developmentSettings, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex("Request\\.Query.*(?:upstream|cluster|host)|(?:upstream|cluster|host).*Request\\.Query", RegexOptions.IgnoreCase), program);
    }

    [Fact]
    public void Frontend_uses_same_origin_relative_api_calls_only()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "src", "Warehouse.Wms.Web", "wwwroot", "app.js"));

        Assert.DoesNotContain("localhost:", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex("https?://", RegexOptions.IgnoreCase), script);
        Assert.DoesNotMatch(new Regex("fetch\\(\\s*['\"](?!/api/|/health/)", RegexOptions.IgnoreCase), script);
    }

    [Theory]
    [InlineData(null, false, "production")]
    [InlineData("http://localhost:5054/", false, "production")]
    [InlineData("http://localhost.:5054/", false, "production")]
    [InlineData("http://LOCALHOST.:5054/", false, "production")]
    [InlineData("ftp://api.example.test/", false, "production")]
    [InlineData("http://127.0.0.1:5054/", false, "production")]
    [InlineData("http://[::1]:5054/", false, "production")]
    public void Production_rejects_untrusted_upstream_configuration(string? upstream, bool allowLoopback, string environmentName)
    {
        var values = new Dictionary<string, string?>
        {
            ["ApiProxy:UpstreamBaseUrl"] = upstream,
            ["ApiProxy:AllowLoopbackUpstream"] = allowLoopback.ToString()
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Throws<InvalidOperationException>(() => WebProxySettings.Validate(configuration, new TestHostEnvironment(environmentName)));
    }

    [Theory]
    [InlineData("http://localhost.:5054/")]
    [InlineData("http://LOCALHOST.:5054/")]
    [InlineData("http://127.0.0.1:5054/")]
    [InlineData("http://[::1]:5054/")]
    public void Production_allows_explicitly_approved_loopback_upstream(string upstream)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiProxy:UpstreamBaseUrl"] = upstream,
            ["ApiProxy:AllowLoopbackUpstream"] = "true"
        }).Build();

        var result = WebProxySettings.Validate(configuration, new TestHostEnvironment("Production"));

        Assert.Equal(upstream, result.UpstreamBaseUrl.AbsoluteUri, ignoreCase: true);
    }

    [Fact]
    public async Task Web_health_is_served_without_an_api_upstream()
    {
        await using var factory = new ProxyWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/web/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/not-found")]
    [InlineData("/health/api/live")]
    public async Task Unavailable_api_proxy_routes_return_problem_502_without_spa_fallback(string path)
    {
        await using var factory = new ProxyWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<!doctype html", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_proxy_paths_remain_handled_by_web_static_files_and_spa_fallback()
    {
        await using var factory = new ProxyWebApplicationFactory();
        using var client = factory.CreateClient();

        var staticResponse = await client.GetAsync("/index.html");
        var spaResponse = await client.GetAsync("/workbench/inbound");

        Assert.Equal(HttpStatusCode.OK, staticResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, spaResponse.StatusCode);
        Assert.Contains("立库工作台", await spaResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_invalid_configuration_prevents_web_host_startup()
    {
        using var factory = new ProxyWebApplicationFactory(environmentName: "Production", upstream: "ftp://api.example.test/");

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Warehouse.Wms.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Warehouse.Wms.Web.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ProxyWebApplicationFactory(string environmentName = "Development", string? upstream = null)
        : WebApplicationFactory<warehouseWeb::Warehouse.Wms.Web.ApiProxyConfiguration>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environmentName);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiProxy:UpstreamBaseUrl"] = upstream ?? "http://127.0.0.1:1/"
            }));
        }
    }
}
