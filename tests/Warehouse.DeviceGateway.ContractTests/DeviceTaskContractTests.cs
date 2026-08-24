using System.Reflection;
using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Domain.Devices;

namespace Warehouse.DeviceGateway.ContractTests;

public sealed class DeviceTaskContractTests
{
    [Fact]
    public void Device_task_requires_the_fields_needed_for_idempotent_dispatch()
    {
        var task = new DeviceTask(
            idempotencyKey: "idem-001",
            wmsTaskId: "wms-001",
            deviceId: "plc-01",
            sourceLocation: "A-01-01",
            destinationLocation: "A-02-01",
            loadingPoint: "LP-0",
            protocolVersion: "v1");

        Assert.Equal("idem-001", task.IdempotencyKey);
        Assert.Equal("wms-001", task.WmsTaskId);
        Assert.Equal("plc-01", task.DeviceId);
        Assert.Equal("A-01-01", task.SourceLocation);
        Assert.Equal("A-02-01", task.DestinationLocation);
        Assert.Equal("LP-0", task.LoadingPoint);
        Assert.Equal("v1", task.ProtocolVersion);
    }

    [Fact]
    public void Device_task_rejects_missing_idempotency_identity()
    {
        Assert.Throws<ArgumentException>(() => new DeviceTask(
            idempotencyKey: "",
            wmsTaskId: "wms-001",
            deviceId: "plc-01",
            sourceLocation: null,
            destinationLocation: "A-02-01",
            loadingPoint: "LP-0",
            protocolVersion: "v1"));
    }

    [Fact]
    public void Gateway_contract_exposes_stop_and_status_operations_without_transport_details()
    {
        var methods = typeof(IWarehouseDeviceGateway)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(IWarehouseDeviceGateway.SubmitInboundAsync), methods);
        Assert.Contains(nameof(IWarehouseDeviceGateway.SubmitOutboundAsync), methods);
        Assert.Contains(nameof(IWarehouseDeviceGateway.SubmitTransferAsync), methods);
        Assert.Contains(nameof(IWarehouseDeviceGateway.GetStatusAsync), methods);
        Assert.Contains(nameof(IWarehouseDeviceGateway.TestConnectionAsync), methods);
        Assert.Contains(nameof(IWarehouseDeviceGateway.RequestStopAsync), methods);

        var source = typeof(IWarehouseDeviceGateway).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("NModbus", source);
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", source);
    }

    [Fact]
    public void Result_observation_carries_versioned_polling_or_callback_evidence()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var observation = new DeviceResultObservation(
            deviceTaskNumber: "device-task-001",
            resultVersion: 3,
            status: DeviceOperationStatus.Executing,
            source: DeviceObservationSource.Polling,
            observedAt: observedAt);

        Assert.Equal("device-task-001", observation.DeviceTaskNumber);
        Assert.Equal(3, observation.ResultVersion);
        Assert.Equal(DeviceObservationSource.Polling, observation.Source);
        Assert.Equal(observedAt, observation.ObservedAt);
    }

    [Fact]
    public void Device_capabilities_are_explicit_and_do_not_imply_exactly_once_execution()
    {
        var capabilities = DeviceCapability.TaskKeyDeduplication | DeviceCapability.TaskQuery;

        Assert.True(capabilities.HasFlag(DeviceCapability.TaskKeyDeduplication));
        Assert.True(capabilities.HasFlag(DeviceCapability.TaskQuery));
        Assert.False(capabilities.HasFlag(DeviceCapability.StopControl));
    }

    [Fact]
    public void Operation_result_preserves_unknown_and_physical_unknown_as_different_safety_outcomes()
    {
        var unknown = new DeviceOperationResult("idem-unknown", DeviceOperationStatus.Unknown);
        var physicalUnknown = new DeviceOperationResult("idem-physical", DeviceOperationStatus.PhysicalStateUnknown);

        Assert.Equal(DeviceOperationStatus.Unknown, unknown.Status);
        Assert.Equal(DeviceOperationStatus.PhysicalStateUnknown, physicalUnknown.Status);
        Assert.NotEqual(unknown.Status, physicalUnknown.Status);
    }
}
