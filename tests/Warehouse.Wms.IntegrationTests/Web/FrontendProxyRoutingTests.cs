extern alias warehouseWeb;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
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
    [InlineData("ftp://api.example.test/", false, "production")]
    [InlineData("http://127.0.0.1:5054/", false, "production")]
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

    [Fact]
    public void Production_allows_explicitly_approved_loopback_upstream()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiProxy:UpstreamBaseUrl"] = "http://127.0.0.1:5054/",
            ["ApiProxy:AllowLoopbackUpstream"] = "true"
        }).Build();

        var result = WebProxySettings.Validate(configuration, new TestHostEnvironment("Production"));

        Assert.Equal("http://127.0.0.1:5054/", result.UpstreamBaseUrl.AbsoluteUri);
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
}
