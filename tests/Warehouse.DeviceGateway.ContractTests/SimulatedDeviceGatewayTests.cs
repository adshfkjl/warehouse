using Warehouse.Wms.DeviceGateway;
using Warehouse.Wms.Domain.Devices;

namespace Warehouse.DeviceGateway.ContractTests;

public sealed class SimulatedDeviceGatewayTests
{
    private static DeviceTask CreateTask(string idempotencyKey = "idem-001") => new(
        idempotencyKey,
        wmsTaskId: "wms-001",
        deviceId: "sim-01",
        sourceLocation: "A-01-01",
        destinationLocation: "A-02-01",
        loadingPoint: "LP-0",
        protocolVersion: "v1");

    [Fact]
    public async Task Submit_records_a_command_before_returning_and_reports_completion_status()
    {
        var gateway = new SimulatedDeviceGateway(new SimulationScenario
        {
            SubmissionStatus = DeviceOperationStatus.Accepted,
            CompletionStatus = DeviceOperationStatus.Succeeded
        });

        var result = await gateway.SubmitInboundAsync(CreateTask());
        var observation = await gateway.GetStatusAsync(result.DeviceTaskNumber!);

        Assert.Equal(DeviceOperationStatus.Accepted, result.Status);
        Assert.NotNull(observation);
        Assert.Equal(DeviceOperationStatus.Succeeded, observation!.Status);
        Assert.Equal(1, gateway.GetPhysicalActionCount("idem-001"));
    }

    [Fact]
    public async Task Duplicate_idempotency_key_returns_the_original_result_without_a_second_action()
    {
        var gateway = new SimulatedDeviceGateway();
        var first = await gateway.SubmitOutboundAsync(CreateTask());
        var second = await gateway.SubmitOutboundAsync(CreateTask());

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.DeviceTaskNumber, second.DeviceTaskNumber);
        Assert.Equal(1, gateway.GetPhysicalActionCount("idem-001"));
    }

    [Theory]
    [InlineData(DeviceOperationStatus.Failed)]
    [InlineData(DeviceOperationStatus.TimedOut)]
    [InlineData(DeviceOperationStatus.Offline)]
    [InlineData(DeviceOperationStatus.Unknown)]
    public async Task Configured_fault_outcomes_are_returned_without_being_mapped_to_success(
        DeviceOperationStatus outcome)
    {
        var gateway = new SimulatedDeviceGateway(new SimulationScenario
        {
            SubmissionStatus = outcome,
            CompletionStatus = outcome,
            AlarmCode = outcome == DeviceOperationStatus.Failed ? "SIM-ALARM" : null
        });

        var result = await gateway.SubmitTransferAsync(CreateTask("idem-fault"));

        Assert.Equal(outcome, result.Status);
        Assert.NotEqual(DeviceOperationStatus.Succeeded, result.Status);
        if (outcome == DeviceOperationStatus.Failed)
        {
            Assert.Equal("SIM-ALARM", result.ErrorCode);
        }
    }

    [Theory]
    [InlineData(DeviceOperationStatus.StopConfirmed)]
    [InlineData(DeviceOperationStatus.StopFailed)]
    [InlineData(DeviceOperationStatus.PhysicalStateUnknown)]
    public async Task Stop_outcome_is_explicit(DeviceOperationStatus stopOutcome)
    {
        var gateway = new SimulatedDeviceGateway(new SimulationScenario { StopStatus = stopOutcome });
        var task = CreateTask("idem-stop");
        await gateway.SubmitInboundAsync(task);

        var result = await gateway.RequestStopAsync(task);

        Assert.Equal(stopOutcome, result.Status);
    }

    [Fact]
    public async Task Shared_state_preserves_idempotency_after_gateway_reconstruction()
    {
        var state = new SimulationStateStore();
        var firstGateway = new SimulatedDeviceGateway(state: state);
        var first = await firstGateway.SubmitInboundAsync(CreateTask("idem-restart"));

        var restartedGateway = new SimulatedDeviceGateway(state: state);
        var second = await restartedGateway.SubmitInboundAsync(CreateTask("idem-restart"));

        Assert.Equal(first.DeviceTaskNumber, second.DeviceTaskNumber);
        Assert.Equal(1, restartedGateway.GetPhysicalActionCount("idem-restart"));
    }

    [Fact]
    public async Task Scenario_exposes_capabilities_without_assuming_deduplication()
    {
        var gateway = new SimulatedDeviceGateway(new SimulationScenario
        {
            Capabilities = DeviceCapability.StopControl
        });

        Assert.Equal(DeviceCapability.StopControl, await gateway.GetCapabilitiesAsync());
    }

    [Fact]
    public async Task Without_task_key_deduplication_a_repeat_is_a_second_simulated_action()
    {
        var gateway = new SimulatedDeviceGateway(new SimulationScenario
        {
            Capabilities = DeviceCapability.None
        });

        await gateway.SubmitInboundAsync(CreateTask("idem-no-dedup"));
        await gateway.SubmitInboundAsync(CreateTask("idem-no-dedup"));

        Assert.Equal(2, gateway.GetPhysicalActionCount("idem-no-dedup"));
    }

    [Fact]
    public async Task Without_task_query_capability_status_is_not_invented()
    {
        var gateway = new SimulatedDeviceGateway(new SimulationScenario
        {
            Capabilities = DeviceCapability.TaskKeyDeduplication
        });
        var result = await gateway.SubmitInboundAsync(CreateTask("idem-no-query"));

        Assert.Null(await gateway.GetStatusAsync(result.DeviceTaskNumber!));
    }

    [Fact]
    public async Task Without_stop_control_request_stop_returns_an_explicit_failure()
    {
        var gateway = new SimulatedDeviceGateway(new SimulationScenario
        {
            Capabilities = DeviceCapability.TaskKeyDeduplication
        });
        var task = CreateTask("idem-no-stop");
        await gateway.SubmitInboundAsync(task);

        var result = await gateway.RequestStopAsync(task);

        Assert.Equal(DeviceOperationStatus.Failed, result.Status);
        Assert.Equal("SIM-STOP-UNSUPPORTED", result.ErrorCode);
    }
}
