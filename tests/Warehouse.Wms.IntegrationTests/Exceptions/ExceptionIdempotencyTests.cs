using Warehouse.Wms.Application.Authorization;
using Warehouse.Wms.Application.Exceptions;
using Warehouse.Wms.Domain.Exceptions;

namespace Warehouse.Wms.IntegrationTests.Exceptions;

public sealed class ExceptionIdempotencyTests
{
    [Fact]
    public void Same_source_external_key_and_task_id_is_the_single_active_work_item()
    {
        var service = new ExceptionWorkItemService(
            new TestCurrentUser("system"),
            new AllowingRiskAuthorizationService());
        var taskId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var request = new ExceptionWorkItemRequest(
            "device-gateway",
            ExceptionType.DeviceTimeout,
            ExceptionSeverity.High,
            "device-timeout",
            taskId,
            "T-333",
            "PLC-333",
            ExceptionPhysicalState.Unknown,
            new ExceptionResourceSnapshot(PalletIds: ["P-333"], ResourceLockIds: ["L-333"]),
            "timeout");

        var first = service.CreateOrMerge(request);
        var duplicate = service.CreateOrMerge(request with { DeviceObservation = "duplicate timeout" });

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Single(service.ActiveWorkItems);
        Assert.Equal(2, duplicate.AuditTrail.Count);
    }

    private sealed class TestCurrentUser(string userId) : ICurrentUser
    {
        public string UserId { get; } = userId;
    }

    private sealed class AllowingRiskAuthorizationService : IRiskAuthorizationService
    {
        public Task<bool> AuthorizeAsync(string operation, ICurrentUser user, string taskNumber, string reason, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
