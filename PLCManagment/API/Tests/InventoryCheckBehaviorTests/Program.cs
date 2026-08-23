using System.Reflection;
using Microsoft.Extensions.Logging;
using PLCManagement.API.Interfaces;
using PLCManagement.API.Models;
using PLCManagement.API.Services;

await InventoryCheckBehaviorTests.RunAll();
Console.WriteLine("Inventory check behavior tests passed.");

internal static class InventoryCheckBehaviorTests
{
    public static async Task RunAll()
    {
        await OutboundCompletionSyncsPalletBeforeReportingSuccess();
        await OutboundCompletionFailsWhenPalletSyncFails();
        TransientPlcUnavailableMessagesAreWaited();
    }

    private static async Task OutboundCompletionSyncsPalletBeforeReportingSuccess()
    {
        var calls = new List<string>();
        var plc = TestProxy<IPlcService>.Create((method, args) =>
        {
            if (method.Name == nameof(IPlcService.UpdatePlcStatusAsync))
            {
                calls.Add("status");
                return Task.FromResult(CreatePlcConfiguration(operationResult: 6));
            }

            throw new NotSupportedException(method.Name);
        });
        var locations = TestProxy<ILocationCheckService>.Create((method, args) =>
        {
            if (method.Name == nameof(ILocationCheckService.UpdateLoadingPointPallet))
            {
                calls.Add("sync");
                return Task.FromResult(true);
            }

            throw new NotSupportedException(method.Name);
        });

        var result = await InvokeWaitForOutboundCompletion(plc, locations);

        Assert(result.IsSuccess, "Outbound completion should succeed after PLC completion and pallet sync.");
        Assert(
            calls.SequenceEqual(new[] { "status", "sync" }),
            $"Expected PLC status to be read before pallet sync, actual: {string.Join(",", calls)}.");
    }

    private static async Task OutboundCompletionFailsWhenPalletSyncFails()
    {
        var plc = TestProxy<IPlcService>.Create((method, args) =>
        {
            if (method.Name == nameof(IPlcService.UpdatePlcStatusAsync))
            {
                return Task.FromResult(CreatePlcConfiguration(operationResult: 0));
            }

            throw new NotSupportedException(method.Name);
        });
        var locations = TestProxy<ILocationCheckService>.Create((method, args) =>
        {
            if (method.Name == nameof(ILocationCheckService.UpdateLoadingPointPallet))
            {
                return Task.FromResult(false);
            }

            throw new NotSupportedException(method.Name);
        });

        var result = await InvokeWaitForOutboundCompletion(plc, locations);

        Assert(!result.IsSuccess, "Outbound completion must fail when loading point pallet sync fails.");
        Assert(
            result.Message.Contains("did not accept tray", StringComparison.OrdinalIgnoreCase),
            $"Expected sync failure message, actual: {result.Message}");
    }

    private static async Task<(bool IsSuccess, string Message)> InvokeWaitForOutboundCompletion(
        IPlcService plcService,
        ILocationCheckService locationCheckService)
    {
        var service = new InventoryCheckService(null!, new TestLogger<InventoryCheckService>());
        var method = typeof(InventoryCheckService).GetMethod(
            "WaitForOutboundCompletionAndSyncPallet",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert(method is not null, "WaitForOutboundCompletionAndSyncPallet must exist.");

        var task = (Task<(bool IsSuccess, string Message)>)method!.Invoke(
            service,
            new object[] { plcService, locationCheckService, "A1", 0, "A10-701" })!;

        return await task;
    }

    private static void TransientPlcUnavailableMessagesAreWaited()
    {
        var service = new InventoryCheckService(null!, new TestLogger<InventoryCheckService>());
        var method = typeof(InventoryCheckService).GetMethod(
            "IsTransientPlcUnavailable",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert(method is not null, "IsTransientPlcUnavailable must exist.");

        var offline = (bool)method!.Invoke(
            service,
            new object?[] { "PLC A3 is offline. Retry after 4 seconds. Last error: Modbus probe failed" })!;
        var plcBusy = (bool)method.Invoke(
            service,
            new object?[] { "\u8bbe\u5907\u5f53\u524d\u4efb\u52a1\u672a\u5b8c\u6210\uff0c\u4e0d\u80fd\u6267\u884c\u65b0\u4efb\u52a1\u3002" })!;
        var businessFailure = (bool)method.Invoke(
            service,
            new object?[] { "指定储位没有货物，不能执行出库" })!;

        Assert(offline, "PLC offline retry messages must be treated as transient wait conditions.");
        Assert(plcBusy, "PLC busy messages must be treated as transient wait conditions during inventory checks.");
        Assert(!businessFailure, "Business validation failures must not be treated as transient PLC outages.");
    }

    private static PlcConfiguration CreatePlcConfiguration(int operationResult)
    {
        return new PlcConfiguration
        {
            PlcId = "A1",
            IpAddress = "127.0.0.1",
            Port = 502,
            SlaveId = 1,
            IsActive = true,
            OperationResult = operationResult
        };
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal class TestProxy<T> : DispatchProxy where T : class
{
    private Func<MethodInfo, object?[], object?>? _handler;

    public static T Create(Func<MethodInfo, object?[], object?> handler)
    {
        var proxy = DispatchProxy.Create<T, TestProxy<T>>();
        ((TestProxy<T>)(object)proxy)._handler = handler;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null || _handler is null)
        {
            throw new InvalidOperationException("Test proxy is not configured.");
        }

        return _handler(targetMethod, args ?? Array.Empty<object?>());
    }
}

internal sealed class TestLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
    }
}
