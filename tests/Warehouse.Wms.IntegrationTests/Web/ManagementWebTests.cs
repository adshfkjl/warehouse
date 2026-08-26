namespace Warehouse.Wms.IntegrationTests.Web;

public sealed class ManagementWebTests
{
    [Fact]
    public void Web_project_contains_the_operational_workbench_and_safe_pda_shell()
    {
        var root = FindRepositoryRoot();
        var index = File.ReadAllText(Path.Combine(root, "src", "Warehouse.Wms.Web", "wwwroot", "index.html"));
        var script = File.ReadAllText(Path.Combine(root, "src", "Warehouse.Wms.Web", "wwwroot", "app.js"));

        Assert.Contains("立库工作台", index);
        Assert.Contains("data-view=\"dashboard\"", index);
        Assert.Contains("data-view=\"inbound\"", index);
        Assert.Contains("data-view=\"outbound\"", index);
        Assert.Contains("data-view=\"inventory\"", index);
        Assert.Contains("data-view=\"tasks\"", index);
        Assert.Contains("data-view=\"stocktaking\"", index);
        Assert.Contains("data-view=\"exceptions\"", index);
        Assert.Contains("pda-mode", index);
        Assert.Contains("PLC", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("register", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("localStorage", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Development_smoke_ports_are_documented_but_dynamic_ports_are_reserved_for_proxy_tests()
    {
        var root = FindRepositoryRoot();
        var development = File.ReadAllText(Path.Combine(root, "docs", "development.md"));
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var proxyTests = File.ReadAllText(Path.Combine(root, "tests", "Warehouse.Wms.IntegrationTests", "Web", "FrontendProxyIntegrationTests.cs"));

        Assert.Contains("localhost:5054", development, StringComparison.Ordinal);
        Assert.Contains("localhost:5055", readme, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:0", proxyTests, StringComparison.Ordinal);
        Assert.DoesNotContain("5054", proxyTests, StringComparison.Ordinal);
        Assert.DoesNotContain("5055", proxyTests, StringComparison.Ordinal);
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
}
