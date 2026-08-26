using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Warehouse.Wms.Web;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateBuilder(args);
var apiProxy = ApiProxySettings.Validate(builder.Configuration, builder.Environment);

var routes = new[]
{
    new RouteConfig
    {
        RouteId = "api",
        ClusterId = "api-upstream",
        Match = new RouteMatch { Path = "/api/{**catch-all}" }
    },
    new RouteConfig
    {
        RouteId = "api-health",
        ClusterId = "api-upstream",
        Match = new RouteMatch { Path = "/health/api/{**catch-all}" },
        Transforms = new[]
        {
            new Dictionary<string, string> { ["PathPattern"] = "/health/{**catch-all}" }
        }
    }
};
var clusters = new[]
{
    new ClusterConfig
    {
        ClusterId = "api-upstream",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["primary"] = new() { Address = apiProxy.UpstreamBaseUrl.AbsoluteUri }
        },
        HttpRequest = new ForwarderRequestConfig
        {
            ActivityTimeout = apiProxy.RequestTimeout
        }
    }
};

builder.Services.AddReverseProxy().LoadFromMemory(routes, clusters);

var app = builder.Build();

app.MapGet("/health/web/live", () => Results.Ok(new { status = "live" }));
app.MapReverseProxy(proxyPipeline =>
{
    // YARP forwards the original method, body, query string, cancellation token, and end-to-end headers.
    // It has no automatic retry policy, so non-idempotent requests are forwarded at most once.
    proxyPipeline.Use(async (context, next) =>
    {
        await next();

        var error = context.Features.Get<IForwarderErrorFeature>()?.Error;
        if (error is ForwarderError.None or null || context.Response.HasStarted)
        {
            return;
        }

        var statusCode = error == ForwarderError.RequestTimedOut
            ? StatusCodes.Status504GatewayTimeout
            : StatusCodes.Status502BadGateway;
        var title = statusCode == StatusCodes.Status504GatewayTimeout
            ? "API upstream timed out."
            : "API upstream is unavailable.";

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title
        };
        await JsonSerializer.SerializeAsync(context.Response.Body, problem, cancellationToken: context.RequestAborted);
    });
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.Run();

namespace Warehouse.Wms.Web
{
    public static class ApiProxySettings
    {
        public static ApiProxyConfiguration Validate(IConfiguration configuration, IHostEnvironment environment)
        {
            const string upstreamKey = "ApiProxy:UpstreamBaseUrl";
            const string developmentDefault = "http://localhost:5054/";
            var rawUpstream = configuration[upstreamKey];

            if (string.IsNullOrWhiteSpace(rawUpstream))
            {
                throw new InvalidOperationException($"{upstreamKey} must be configured.");
            }

            if (!Uri.TryCreate(rawUpstream, UriKind.Absolute, out var upstream)
                || (upstream.Scheme != Uri.UriSchemeHttp && upstream.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(upstream.UserInfo)
                || !string.IsNullOrEmpty(upstream.Query)
                || !string.IsNullOrEmpty(upstream.Fragment))
            {
                throw new InvalidOperationException($"{upstreamKey} must be an absolute HTTP or HTTPS address without credentials, query, or fragment.");
            }

            if (environment.IsProduction() && string.Equals(upstream.AbsoluteUri, developmentDefault, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{upstreamKey} cannot use the development default in production.");
            }

            if (environment.IsProduction() && upstream.IsLoopback && !configuration.GetValue<bool>("ApiProxy:AllowLoopbackUpstream"))
            {
                throw new InvalidOperationException("A loopback API upstream requires ApiProxy:AllowLoopbackUpstream=true in production.");
            }

            var timeoutSeconds = configuration.GetValue("ApiProxy:RequestTimeoutSeconds", 30);
            if (timeoutSeconds <= 0)
            {
                throw new InvalidOperationException("ApiProxy:RequestTimeoutSeconds must be positive.");
            }

            return new ApiProxyConfiguration(upstream, TimeSpan.FromSeconds(timeoutSeconds));
        }
    }

    public sealed record ApiProxyConfiguration(Uri UpstreamBaseUrl, TimeSpan RequestTimeout);
}
