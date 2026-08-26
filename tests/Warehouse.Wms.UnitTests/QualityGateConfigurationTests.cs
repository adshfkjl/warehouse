using System.Text.RegularExpressions;

namespace Warehouse.Wms.UnitTests;

public sealed class QualityGateConfigurationTests
{
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Warehouse.Wms.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    [Fact]
    public void Required_development_files_exist()
    {
        var root = FindRepositoryRoot();
        var requiredFiles = new[]
        {
            ".editorconfig",
            "Directory.Build.props",
            "docker-compose.dev.yml",
            Path.Combine("docs", "development.md"),
            Path.Combine("scripts", "verify.ps1"),
            Path.Combine("scripts", "verify-legacy-source.ps1"),
            Path.Combine("docs", "legacy-source-manifest.sha256")
        };

        Assert.All(requiredFiles, file => Assert.True(
            File.Exists(Path.Combine(root, file)),
            $"Missing Task 1.2 file: {file}"));
    }

    [Fact]
    public void Verification_script_runs_checks_in_required_order()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "verify.ps1"));
        var commands = new[]
        {
            "STEP 1 - restore",
            "STEP 2 - build",
            "STEP 3 - test",
            "STEP 4 - legacy-source-integrity",
            "STEP 5 - migration",
            "STEP 6 - health",
            "STEP 7 - warehouse-protection"
        };
        var positions = commands.Select(command => Regex.Match(
            script,
            $"(?im)^.*{Regex.Escape(command)}.*$" ).Index).ToArray();

        Assert.DoesNotContain(positions, position => position < 0);
        Assert.True(positions.SequenceEqual(positions.OrderBy(position => position)),
            "verify.ps1 must run restore, build, test, legacy source integrity, migration, health, then warehouse protection checks.");

        Assert.Contains("verify-legacy-source.ps1", script);
    }

    [Fact]
    public void Verification_script_groups_migration_path_check_before_boolean_operators()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "verify.ps1"));

        Assert.Contains("$hasMigrations = (Test-Path", script);
    }

    [Fact]
    public void Verification_script_uses_explicit_development_in_memory_configuration_for_health_check()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "verify.ps1"));

        Assert.Contains("$env:ASPNETCORE_ENVIRONMENT = \"Development\"", script);
        Assert.Contains("$env:Wms__PersistenceMode = \"InMemory\"", script);
    }
}
