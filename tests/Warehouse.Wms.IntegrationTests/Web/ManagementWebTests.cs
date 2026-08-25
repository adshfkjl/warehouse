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
