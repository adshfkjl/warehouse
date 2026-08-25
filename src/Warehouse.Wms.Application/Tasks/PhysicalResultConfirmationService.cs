using Warehouse.Wms.Application.Authorization;
using Warehouse.Wms.Domain.Tasks;

namespace Warehouse.Wms.Application.Tasks;

public sealed record InventoryCorrectionJournal(
    string? JournalNumber,
    string? ItemCode,
    decimal? Quantity,
    string? Unit,
    string? BeforeLocation,
    string? AfterLocation,
    string? Reason);

public sealed record PhysicalResultConfirmationRequest(
    string? Reason,
    string? DeviceStatus,
    string? ActualPalletLocation,
    string? SourceLocation,
    string? DestinationLocation,
    InventoryCorrectionJournal? InventoryCorrection,
    string? PalletNumber = null);

public sealed record PhysicalResultConfirmation(
    Guid TaskId,
    string TaskNumber,
    TaskState State,
    string ConfirmedBy,
    string Reason,
    string DeviceStatus,
    string ActualPalletLocation,
    string SourceLocation,
    string DestinationLocation,
    InventoryCorrectionJournal InventoryCorrection,
    DateTimeOffset ConfirmedAt,
    string? PalletNumber);

/// <summary>
/// Records a human-confirmed physical outcome as an auditable intervention.
/// It deliberately transitions to ManualIntervention instead of pretending that
/// an operator observation is an automatic device success.
/// </summary>
public sealed class PhysicalResultConfirmationService
{
    public const string AuthorizationOperation = "Task.ManualPhysicalResultConfirmation";
    public const string AuditReason = "人工确认物理结果并结案";

    private static readonly HashSet<TaskState> ConfirmableStates =
    [
        TaskState.Failed,
        TaskState.TimedOut,
        TaskState.StopConfirmed,
        TaskState.StopFailed,
        TaskState.PhysicalStateUnknown
    ];

    private readonly ICurrentUser _currentUser;
    private readonly IRiskAuthorizationService _riskAuthorization;
    private readonly Dictionary<Guid, PhysicalResultConfirmation> _confirmations = [];
    private readonly object _gate = new();

    public PhysicalResultConfirmationService(
        ICurrentUser currentUser,
        IRiskAuthorizationService riskAuthorization)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _riskAuthorization = riskAuthorization ?? throw new ArgumentNullException(nameof(riskAuthorization));
    }

    public IReadOnlyCollection<PhysicalResultConfirmation> Confirmations
    {
        get
        {
            lock (_gate)
            {
                return _confirmations.Values.ToArray();
            }
        }
    }

    public async Task<PhysicalResultConfirmation> ConfirmAsync(
        WarehouseTask task,
        PhysicalResultConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(request);

        var userId = Require(_currentUser.UserId, nameof(_currentUser.UserId));
        ValidateTaskState(task);
        ValidateRequest(request);

        lock (_gate)
        {
            if (_confirmations.ContainsKey(task.Id))
            {
                throw new InvalidOperationException($"Task '{task.TaskNumber}' has already been confirmed.");
            }
        }

        var authorized = await _riskAuthorization.AuthorizeAsync(
            AuthorizationOperation,
            _currentUser,
            task.TaskNumber,
            request.Reason!.Trim(),
            cancellationToken);
        if (!authorized)
        {
            throw new UnauthorizedAccessException(
                $"Second authorization was denied for task '{task.TaskNumber}'.");
        }

        // Re-check after the external authorization call so a concurrent confirmer
        // cannot turn a second approval into a duplicate physical conclusion.
        lock (_gate)
        {
            if (_confirmations.ContainsKey(task.Id) || task.State == TaskState.ManualIntervention)
            {
                throw new InvalidOperationException($"Task '{task.TaskNumber}' has already been confirmed.");
            }

            task.TransitionTo(TaskState.ManualIntervention, userId, AuditReason, "MANUAL_PHYSICAL_CONFIRMATION");
            var confirmation = new PhysicalResultConfirmation(
                task.Id,
                task.TaskNumber,
                task.State,
                userId,
                request.Reason.Trim(),
                request.DeviceStatus!.Trim(),
                request.ActualPalletLocation!.Trim(),
                request.SourceLocation!.Trim(),
                request.DestinationLocation!.Trim(),
                NormalizeJournal(request.InventoryCorrection!),
                DateTimeOffset.UtcNow,
                request.PalletNumber?.Trim());
            _confirmations.Add(task.Id, confirmation);
            return confirmation;
        }
    }

    private static void ValidateTaskState(WarehouseTask task)
    {
        if (task.State == TaskState.ManualIntervention)
        {
            throw new InvalidOperationException($"Task '{task.TaskNumber}' has already been confirmed.");
        }

        if (!ConfirmableStates.Contains(task.State))
        {
            throw new InvalidOperationException(
                $"Task '{task.TaskNumber}' cannot be manually confirmed from state '{task.State}'.");
        }
    }

    private static void ValidateRequest(PhysicalResultConfirmationRequest request)
    {
        Require(request.Reason, nameof(request.Reason));
        Require(request.DeviceStatus, nameof(request.DeviceStatus));
        Require(request.ActualPalletLocation, nameof(request.ActualPalletLocation));
        Require(request.SourceLocation, nameof(request.SourceLocation));
        Require(request.DestinationLocation, nameof(request.DestinationLocation));

        var journal = request.InventoryCorrection
            ?? throw new ArgumentException("Inventory correction journal is required.", nameof(request));
        Require(journal.JournalNumber, "InventoryCorrection.JournalNumber");
        Require(journal.ItemCode, "InventoryCorrection.ItemCode");
        if (!journal.Quantity.HasValue || journal.Quantity.Value == 0)
        {
            throw new ArgumentException("A non-zero inventory correction quantity is required.", nameof(request));
        }

        Require(journal.Unit, "InventoryCorrection.Unit");
        Require(journal.BeforeLocation, "InventoryCorrection.BeforeLocation");
        Require(journal.AfterLocation, "InventoryCorrection.AfterLocation");
        Require(journal.Reason, "InventoryCorrection.Reason");
    }

    private static InventoryCorrectionJournal NormalizeJournal(InventoryCorrectionJournal journal)
        => new(
            journal.JournalNumber!.Trim(),
            journal.ItemCode!.Trim(),
            journal.Quantity,
            journal.Unit!.Trim(),
            journal.BeforeLocation!.Trim(),
            journal.AfterLocation!.Trim(),
            journal.Reason!.Trim());

    private static string Require(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value;
    }
}
