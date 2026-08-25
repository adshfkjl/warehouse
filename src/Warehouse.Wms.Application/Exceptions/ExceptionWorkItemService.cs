using Warehouse.Wms.Application.Authorization;
using Warehouse.Wms.Domain.Exceptions;

namespace Warehouse.Wms.Application.Exceptions;

public sealed record ExceptionWorkItemRequest(
    string Source,
    ExceptionType Type,
    ExceptionSeverity Severity,
    string ExternalKey,
    Guid TaskId,
    string TaskNumber,
    string? DeviceTaskNumber,
    ExceptionPhysicalState PhysicalState,
    ExceptionResourceSnapshot Resources,
    string? DeviceObservation);

public sealed record ExceptionActionRequest(ExceptionAction Action, string Reason);

public interface IExceptionActionExecutor
{
    Task<ExceptionActionOutcome> ExecuteAsync(
        ExceptionWorkItem item,
        ExceptionActionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryExceptionActionExecutor : IExceptionActionExecutor
{
    public Task<ExceptionActionOutcome> ExecuteAsync(
        ExceptionWorkItem item,
        ExceptionActionRequest request,
        CancellationToken cancellationToken = default)
    {
        var status = request.Action == ExceptionAction.Close
            ? ExceptionWorkItemStatus.Closed
            : request.Action == ExceptionAction.ConfirmPhysicalResult
                ? ExceptionWorkItemStatus.Resolved
                : ExceptionWorkItemStatus.InProgress;
        var physicalState = request.Action == ExceptionAction.ConfirmPhysicalResult
            ? ExceptionPhysicalState.Confirmed
            : item.PhysicalState;
        return Task.FromResult(new ExceptionActionOutcome(status, physicalState, false, "accepted by in-memory adapter"));
    }
}

/// <summary>
/// First-version exception center backed by an explicit in-memory store.
/// A host must replace the store and action executor with transactional adapters
/// before production use; this class intentionally cannot claim durable recovery.
/// </summary>
public sealed class ExceptionWorkItemService
{
    public const string ConfirmationAuthorizationOperation = "Exception.ConfirmPhysicalResult";
    public const string InventoryCorrectionAuthorizationOperation = "Exception.InventoryCorrection";
    public const string StopAuthorizationOperation = "Exception.RequestStop";

    private readonly ICurrentUser _currentUser;
    private readonly IRiskAuthorizationService _riskAuthorization;
    private readonly IExceptionActionExecutor _actionExecutor;
    private readonly Dictionary<string, ExceptionWorkItem> _byIdentity = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _inFlight = [];
    private readonly object _gate = new();

    public ExceptionWorkItemService(
        ICurrentUser currentUser,
        IRiskAuthorizationService riskAuthorization,
        IExceptionActionExecutor? actionExecutor = null)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _riskAuthorization = riskAuthorization ?? throw new ArgumentNullException(nameof(riskAuthorization));
        _actionExecutor = actionExecutor ?? new InMemoryExceptionActionExecutor();
    }

    public IReadOnlyCollection<ExceptionWorkItem> ActiveWorkItems
    {
        get
        {
            lock (_gate)
            {
                return _byIdentity.Values
                    .Where(item => item.Status != ExceptionWorkItemStatus.Closed)
                    .ToArray();
            }
        }
    }

    public IReadOnlyCollection<ExceptionWorkItem> WorkItems
    {
        get
        {
            lock (_gate)
            {
                return _byIdentity.Values.ToArray();
            }
        }
    }

    public ExceptionWorkItem CreateOrMerge(ExceptionWorkItemRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var userId = RequireUser();
        var key = IdentityKey(request);

        lock (_gate)
        {
            if (_byIdentity.TryGetValue(key, out var existing))
            {
                if (existing.Status == ExceptionWorkItemStatus.Closed)
                {
                    existing.Reopen(request.Type, request.Severity, request.DeviceTaskNumber,
                        request.PhysicalState, request.Resources, request.DeviceObservation,
                        userId, "closed exception re-opened by a new alert");
                }
                else
                {
                    existing.Merge(request.Type, request.Severity, request.DeviceTaskNumber,
                        request.PhysicalState, request.Resources, request.DeviceObservation,
                        userId, "duplicate alert merged");
                }

                return existing;
            }

            var item = new ExceptionWorkItem(
                request.Source,
                request.Type,
                request.Severity,
                request.ExternalKey,
                request.TaskId,
                request.TaskNumber,
                request.DeviceTaskNumber,
                request.PhysicalState,
                request.Resources,
                request.DeviceObservation);
            item.AddAlertedAudit(userId, "exception work item created", request.DeviceObservation);
            _byIdentity.Add(key, item);
            return item;
        }
    }

    public async Task<ExceptionWorkItem> ExecuteAsync(
        Guid id,
        ExceptionActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Require(request.Reason, nameof(request.Reason));
        var userId = RequireUser();
        ExceptionWorkItem item;
        lock (_gate)
        {
            item = _byIdentity.Values.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new KeyNotFoundException($"Exception work item '{id}' was not found.");
            if (item.Status == ExceptionWorkItemStatus.Closed)
            {
                throw new InvalidOperationException($"Exception work item '{id}' is closed.");
            }

            if (!_inFlight.Add(item.Id))
            {
                throw new InvalidOperationException($"Exception work item '{id}' already has an action in progress.");
            }

            if (item.PhysicalState == ExceptionPhysicalState.Unknown
                && request.Action is ExceptionAction.Retry or ExceptionAction.Reassign)
            {
                item.AddRejectedAudit(request.Action, userId, reason, "physical state remains unknown");
                _inFlight.Remove(item.Id);
                throw new InvalidOperationException(
                    "A physical-state-unknown task must be reconciled before retry or reassignment.");
            }
        }

        try
        {
            if (RequiresSecondAuthorization(request.Action))
            {
                var authorized = await _riskAuthorization.AuthorizeAsync(
                    AuthorizationOperation(request.Action),
                    _currentUser,
                    item.TaskNumber,
                    reason,
                    cancellationToken);
                if (!authorized)
                {
                    lock (_gate)
                    {
                        item.AddRejectedAudit(request.Action, userId, reason, "second authorization denied");
                    }

                    throw new UnauthorizedAccessException($"Second authorization was denied for task '{item.TaskNumber}'.");
                }
            }

            var outcome = await _actionExecutor.ExecuteAsync(item, request, cancellationToken);
            if (request.Action == ExceptionAction.Close && outcome.Status != ExceptionWorkItemStatus.Closed)
            {
                outcome = outcome with { Status = ExceptionWorkItemStatus.Closed };
            }

            lock (_gate)
            {
                if (item.Status == ExceptionWorkItemStatus.Closed)
                {
                    throw new InvalidOperationException($"Exception work item '{id}' was closed concurrently.");
                }

                item.ApplyOutcome(request.Action, outcome, userId, reason);
                return item;
            }
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(item.Id);
            }
        }
    }

    private static bool RequiresSecondAuthorization(ExceptionAction action)
        => action is ExceptionAction.RequestStop
            or ExceptionAction.ConfirmPhysicalResult
            or ExceptionAction.InventoryCorrection;

    private static string AuthorizationOperation(ExceptionAction action)
        => action switch
        {
            ExceptionAction.RequestStop => StopAuthorizationOperation,
            ExceptionAction.ConfirmPhysicalResult => ConfirmationAuthorizationOperation,
            ExceptionAction.InventoryCorrection => InventoryCorrectionAuthorizationOperation,
            _ => "Exception." + action
        };

    private string RequireUser() => Require(_currentUser.UserId, nameof(_currentUser.UserId));

    private static string IdentityKey(ExceptionWorkItemRequest request)
        => $"{Require(request.Source, nameof(request.Source))}|{Require(request.ExternalKey, nameof(request.ExternalKey))}|{request.TaskId:D}";

    private static void ValidateRequest(ExceptionWorkItemRequest request)
    {
        Require(request.Source, nameof(request.Source));
        Require(request.ExternalKey, nameof(request.ExternalKey));
        Require(request.TaskNumber, nameof(request.TaskNumber));
        if (request.TaskId == Guid.Empty)
        {
            throw new ArgumentException("A task id is required.", nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Resources);
    }

    private static string Require(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value.Trim();
    }
}
