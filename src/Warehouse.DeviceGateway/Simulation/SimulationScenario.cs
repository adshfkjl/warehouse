using System.Collections.Concurrent;
using Warehouse.Wms.Domain.Devices;

namespace Warehouse.Wms.DeviceGateway;

public sealed class SimulationScenario
{
    public TimeSpan ResponseDelay { get; init; } = TimeSpan.Zero;

    public DeviceOperationStatus SubmissionStatus { get; init; } = DeviceOperationStatus.Accepted;

    public DeviceOperationStatus CompletionStatus { get; init; } = DeviceOperationStatus.Succeeded;

    public DeviceOperationStatus StopStatus { get; init; } = DeviceOperationStatus.StopConfirmed;

    public DeviceOperationStatus ConnectionStatus { get; init; } = DeviceOperationStatus.Succeeded;

    public DeviceCapability Capabilities { get; init; } =
        DeviceCapability.TaskKeyDeduplication |
        DeviceCapability.TaskQuery |
        DeviceCapability.StopControl;

    public string? AlarmCode { get; init; }
}

public sealed class SimulationStateStore
{
    internal ConcurrentDictionary<string, SimulatedCommand> Commands { get; } = new(StringComparer.Ordinal);

    internal int CountActions(string idempotencyKey)
    {
        return Commands.Values.Count(command =>
            string.Equals(command.Task.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
    }
}

internal sealed record SimulatedCommand(
    DeviceTask Task,
    string DeviceTaskNumber,
    DeviceOperationResult SubmissionResult,
    DeviceOperationStatus CompletionStatus,
    DeviceOperationStatus StopStatus,
    string? AlarmCode,
    int PhysicalActionCount = 1);
