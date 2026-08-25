using Warehouse.Wms.Application.Authorization;
using Warehouse.Wms.Application.Exceptions;
using Warehouse.Wms.Domain.Exceptions;

namespace Warehouse.Wms.UnitTests.Exceptions;

public sealed class ExceptionWorkItemTests
{
    [Fact]
    public void Creates_work_item_with_physical_context_and_audit()
    {
        var service = CreateService();

        var item = service.CreateOrMerge(new ExceptionWorkItemRequest(
            Source: "device-gateway",
            Type: ExceptionType.PhysicalStateUnknown,
            Severity: ExceptionSeverity.Critical,
            ExternalKey: "timeout-17",
            TaskId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TaskNumber: "T-17",
            DeviceTaskNumber: "PLC-17",
            PhysicalState: ExceptionPhysicalState.Unknown,
            Resources: new ExceptionResourceSnapshot(
                PalletIds: ["P-17"],
                LocationIds: ["A-01"],
                LoadingPointIds: ["LP-1"],
                InventoryBalanceIds: ["INV-17"],
                ResourceLockIds: ["LOCK-17"]),
            DeviceObservation: "query unavailable"));

        Assert.Equal(ExceptionWorkItemStatus.Active, item.Status);
        Assert.Equal(ExceptionPhysicalState.Unknown, item.PhysicalState);
        Assert.Equal("PLC-17", item.DeviceTaskNumber);
        Assert.Contains("P-17", item.Resources.PalletIds);
        Assert.Single(item.AuditTrail);
        Assert.Equal(ExceptionAction.Alerted, item.AuditTrail[0].Action);
    }

    [Fact]
    public void Duplicate_alert_is_merged_and_does_not_create_another_active_item()
    {
        var service = CreateService();
        var request = Request("duplicate", Guid.Parse("22222222-2222-2222-2222-222222222222"));

        var first = service.CreateOrMerge(request);
        var second = service.CreateOrMerge(request with { DeviceObservation = "same task observed again" });

        Assert.Equal(first.Id, second.Id);
        Assert.Single(service.ActiveWorkItems);
        Assert.Contains(second.AuditTrail, audit => audit.Action == ExceptionAction.Merged);
    }

    [Fact]
    public async Task Physical_unknown_cannot_be_resolved_as_success_or_release_resources()
    {
        var service = CreateService(new RecordingActionExecutor
        {
            Outcome = new ExceptionActionOutcome(
                ExceptionWorkItemStatus.Resolved,
                ExceptionPhysicalState.Unknown,
                ResourcesReleased: true,
                "device still unknown")
        });
        var item = service.CreateOrMerge(Request("unknown", Guid.NewGuid()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(
            item.Id,
            new ExceptionActionRequest(ExceptionAction.Retry, "retry should be blocked")));

        Assert.Equal(ExceptionWorkItemStatus.Active, item.Status);
        Assert.DoesNotContain(item.AuditTrail, audit => audit.Action == ExceptionAction.Retry);
    }

    [Fact]
    public async Task Physical_unknown_cannot_be_closed_without_confirmed_physical_state()
    {
        var service = CreateService(new RecordingActionExecutor
        {
            Outcome = new ExceptionActionOutcome(
                ExceptionWorkItemStatus.Closed,
                ExceptionPhysicalState.Unknown,
                ResourcesReleased: false,
                "still unknown")
        });
        var item = service.CreateOrMerge(Request("unknown-close", Guid.NewGuid()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(
            item.Id,
            new ExceptionActionRequest(ExceptionAction.Close, "cannot close without physical confirmation")));

        Assert.Equal(ExceptionWorkItemStatus.Active, item.Status);
    }

    [Fact]
    public async Task Physical_confirmation_requires_second_authorization()
    {
        var service = CreateService(new RecordingActionExecutor
        {
            Outcome = new ExceptionActionOutcome(
                ExceptionWorkItemStatus.Resolved,
                ExceptionPhysicalState.Confirmed,
                ResourcesReleased: false,
                "operator verified location")
        }, new DenyingRiskAuthorizationService());
        var item = service.CreateOrMerge(Request("auth", Guid.NewGuid()));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ExecuteAsync(
            item.Id,
            new ExceptionActionRequest(ExceptionAction.ConfirmPhysicalResult, "manual confirmation")));

        Assert.Equal(ExceptionWorkItemStatus.Active, item.Status);
    }

    [Fact]
    public async Task Closed_work_item_reopens_when_same_alert_arrives_again()
    {
        var executor = new RecordingActionExecutor
        {
            Outcome = new ExceptionActionOutcome(
                ExceptionWorkItemStatus.Resolved,
                ExceptionPhysicalState.Failed,
                ResourcesReleased: false,
                "reconciled")
        };
        var service = CreateService(executor);
        var request = Request("reopen", Guid.NewGuid()) with { PhysicalState = ExceptionPhysicalState.Known };
        var item = service.CreateOrMerge(request);

        await service.ExecuteAsync(item.Id, new ExceptionActionRequest(ExceptionAction.Close, "closed after review"));
        var reopened = service.CreateOrMerge(request with { DeviceObservation = "alert returned" });

        Assert.Equal(item.Id, reopened.Id);
        Assert.Equal(ExceptionWorkItemStatus.Active, reopened.Status);
        Assert.Contains(reopened.AuditTrail, audit => audit.Action == ExceptionAction.Reopened);
    }

    private static ExceptionWorkItemService CreateService(
        IExceptionActionExecutor? executor = null,
        IRiskAuthorizationService? authorization = null)
        => new(
            new TestCurrentUser("operator-1"),
            authorization ?? new AllowingRiskAuthorizationService(),
            executor ?? new RecordingActionExecutor());

    private static ExceptionWorkItemRequest Request(string key, Guid taskId)
        => new(
            "scheduler",
            ExceptionType.TaskFailed,
            ExceptionSeverity.High,
            key,
            taskId,
            "T-" + key,
            "PLC-" + key,
            ExceptionPhysicalState.Unknown,
            new ExceptionResourceSnapshot(ResourceLockIds: ["lock-" + key]),
            "failed");

    private sealed class TestCurrentUser(string userId) : ICurrentUser
    {
        public string UserId { get; } = userId;
    }

    private sealed class AllowingRiskAuthorizationService : IRiskAuthorizationService
    {
        public Task<bool> AuthorizeAsync(string operation, ICurrentUser user, string taskNumber, string reason, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class DenyingRiskAuthorizationService : IRiskAuthorizationService
    {
        public Task<bool> AuthorizeAsync(string operation, ICurrentUser user, string taskNumber, string reason, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class RecordingActionExecutor : IExceptionActionExecutor
    {
        public ExceptionActionOutcome Outcome { get; set; } = new(
            ExceptionWorkItemStatus.InProgress,
            ExceptionPhysicalState.Unknown,
            ResourcesReleased: false,
            "accepted");

        public Task<ExceptionActionOutcome> ExecuteAsync(ExceptionWorkItem item, ExceptionActionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(Outcome);
    }
}
