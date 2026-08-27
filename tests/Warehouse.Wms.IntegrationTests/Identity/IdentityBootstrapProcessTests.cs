using System.Diagnostics;

namespace Warehouse.Wms.IntegrationTests.Identity;

public sealed class IdentityBootstrapProcessTests
{
    [Fact]
    public async Task Create_admin_command_exits_before_starting_the_http_host_when_password_input_is_redirected()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("identity");
        start.ArgumentList.Add("create-admin");
        start.ArgumentList.Add("--username");
        start.ArgumentList.Add("admin");

        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var error = await process.StandardError.ReadToEndAsync();

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("Password input must be an interactive console.", error, StringComparison.Ordinal);
    }
}
