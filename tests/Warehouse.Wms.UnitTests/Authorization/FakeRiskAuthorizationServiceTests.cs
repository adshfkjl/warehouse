using Warehouse.Wms.Application.Authorization;

namespace Warehouse.Wms.UnitTests.Authorization;

public sealed class FakeRiskAuthorizationServiceTests
{
    [Fact]
    public async Task Risk_authorization_receives_operation_user_task_and_reason()
    {
        var user = new TestCurrentUser("operator-01");
        var service = new RecordingRiskAuthorizationService(true);

        var allowed = await service.AuthorizeAsync(
            "Task.ManualPhysicalResultConfirmation",
            user,
            "TASK-001",
            "physical result checked");

        Assert.True(allowed);
        var call = Assert.Single(service.Calls);
        Assert.Equal("Task.ManualPhysicalResultConfirmation", call.Operation);
        Assert.Equal("operator-01", call.UserId);
        Assert.Equal("TASK-001", call.TaskNumber);
        Assert.Equal("physical result checked", call.Reason);
    }

    [Fact]
    public async Task Risk_authorization_can_deny_high_risk_operation()
    {
        var service = new RecordingRiskAuthorizationService(false);

        var allowed = await service.AuthorizeAsync(
            "Task.ManualPhysicalResultConfirmation",
            new TestCurrentUser("operator-01"),
            "TASK-001",
            "physical result checked");

        Assert.False(allowed);
    }

    private sealed record TestCurrentUser(string UserId) : ICurrentUser;

    private sealed class RecordingRiskAuthorizationService(bool result) : IRiskAuthorizationService
    {
        public List<AuthorizationCall> Calls { get; } = [];

        public Task<bool> AuthorizeAsync(
            string operation,
            ICurrentUser user,
            string taskNumber,
            string reason,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new AuthorizationCall(operation, user.UserId, taskNumber, reason));
            return Task.FromResult(result);
        }
    }

    private sealed record AuthorizationCall(string Operation, string UserId, string TaskNumber, string Reason);
}
