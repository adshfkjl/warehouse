using System.Globalization;
using Warehouse.Wms.Domain.Inbound;
using Warehouse.Wms.Domain.MasterData;

namespace Warehouse.Wms.Application.Inbound;

/// <summary>
/// Operational overlay for the deliberately small master-data model. Fault and
/// occupancy are supplied by the WMS/WCS read model until those fields become
/// persistent master-data columns.
/// </summary>
public sealed record PutawayLocationCandidate(
    Location Location,
    bool IsFaulted = false,
    bool IsDisabled = false,
    bool IsLocked = false,
    bool IsOccupied = false,
    int OccupiedUnits = 0,
    decimal OccupiedWeightKg = 0m)
{
    public bool EffectiveDisabled => IsDisabled || Location.IsDisabled;

    public bool EffectiveLocked => IsLocked || Location.IsLocked;

    public bool EffectiveOccupied => IsOccupied || OccupiedUnits > 0;
}

public sealed record PutawayLoadingPointCandidate(
    LoadingPoint LoadingPoint,
    bool HasPallet,
    bool IsFaulted = false,
    bool IsDisabled = false,
    bool IsLocked = false)
{
    public bool EffectiveDisabled => IsDisabled || LoadingPoint.IsDisabled;

    public bool EffectiveLocked => IsLocked || LoadingPoint.IsLocked;
}

public sealed record PutawayAllocationRequest(
    Guid PendingInboundInventoryId,
    decimal LengthMm,
    decimal WidthMm,
    decimal HeightMm,
    decimal WeightKg,
    Guid? RequestedLocationId = null);

public sealed record PutawayAllocation(
    Guid PendingInboundInventoryId,
    Guid LocationId,
    string LocationCode,
    string RecommendationReason,
    DateTimeOffset AllocatedAt);

public sealed class PutawayAllocationService
{
    private readonly object _gate = new();
    private readonly List<PutawayLocationCandidate> _locations;
    private readonly List<PutawayLoadingPointCandidate> _loadingPoints;
    private readonly Dictionary<Guid, PutawayAllocation> _allocations = [];
    private readonly Dictionary<Guid, string> _requestFingerprints = [];

    public PutawayAllocationService(
        IEnumerable<PutawayLocationCandidate>? locations = null,
        IEnumerable<PutawayLoadingPointCandidate>? loadingPoints = null)
    {
        _locations = locations?.ToList() ?? [];
        _loadingPoints = loadingPoints?.ToList() ?? [];
        EnsureUniqueLocations(_locations);
        EnsureUniqueLoadingPoints(_loadingPoints);
    }

    public IReadOnlyCollection<PutawayLocationCandidate> Locations
    {
        get { lock (_gate) return _locations.ToArray(); }
    }

    public IReadOnlyCollection<PutawayLoadingPointCandidate> LoadingPoints
    {
        get { lock (_gate) return _loadingPoints.ToArray(); }
    }

    public IReadOnlyCollection<PutawayAllocation> Allocations
    {
        get { lock (_gate) return _allocations.Values.ToArray(); }
    }

    public PutawayAllocation? Get(Guid pendingInboundInventoryId)
    {
        lock (_gate)
        {
            return _allocations.TryGetValue(pendingInboundInventoryId, out var allocation)
                ? allocation
                : null;
        }
    }

    public PutawayAllocation Allocate(
        PendingInboundInventory pendingInventory,
        PutawayAllocationRequest request,
        DateTimeOffset? allocatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(pendingInventory);
        ArgumentNullException.ThrowIfNull(request);
        if (pendingInventory.Id != request.PendingInboundInventoryId)
        {
            throw new InvalidOperationException("The allocation request does not belong to the pending inbound inventory.");
        }

        if (pendingInventory.Status != Domain.Inventory.InventoryStatus.PendingInbound || pendingInventory.LocationId is not null)
        {
            throw new InvalidOperationException("Only inventory awaiting putaway can be allocated.");
        }

        ValidateDimensions(request);
        var fingerprint = Fingerprint(request);
        lock (_gate)
        {
            if (_allocations.TryGetValue(pendingInventory.Id, out var existing))
            {
                if (!string.Equals(_requestFingerprints[pendingInventory.Id], fingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The pending inbound inventory already has a different putaway allocation.");
                }

                return existing;
            }

            var candidate = request.RequestedLocationId is Guid requestedId
                ? FindRequested(requestedId)
                : FindRecommended(request);
            EnsureAvailable(candidate, request);
            if (_allocations.Values.Any(item => item.LocationId == candidate.Location.Id))
            {
                throw new InvalidOperationException($"Location '{candidate.Location.Code}' is already reserved for putaway.");
            }

            var isManual = request.RequestedLocationId.HasValue;
            var reason = isManual
                ? $"人工指定库位：{candidate.Location.Code}；已校验尺寸、重量和容量"
                : $"自动推荐库位：{candidate.Location.Code}；尺寸、重量和容量符合，按库位编码确定性选择";
            var allocation = new PutawayAllocation(
                pendingInventory.Id,
                candidate.Location.Id,
                candidate.Location.Code,
                reason,
                (allocatedAt ?? DateTimeOffset.UtcNow).ToUniversalTime());
            _allocations.Add(pendingInventory.Id, allocation);
            _requestFingerprints.Add(pendingInventory.Id, fingerprint);
            return allocation;
        }
    }

    public void Release(Guid pendingInboundInventoryId)
    {
        lock (_gate)
        {
            _allocations.Remove(pendingInboundInventoryId);
            _requestFingerprints.Remove(pendingInboundInventoryId);
        }
    }

    public PutawayLoadingPointCandidate SelectLoadingPoint(Guid? requestedLoadingPointId = null)
    {
        lock (_gate)
        {
            PutawayLoadingPointCandidate? candidate;
            if (requestedLoadingPointId is Guid id)
            {
                candidate = _loadingPoints.FirstOrDefault(item => item.LoadingPoint.Id == id)
                    ?? throw new KeyNotFoundException($"Loading point '{id}' was not found.");
                EnsureLoadingPointAvailable(candidate);
                return candidate;
            }

            candidate = _loadingPoints
                .Where(item => item.HasPallet && !item.IsFaulted && !item.EffectiveDisabled && !item.EffectiveLocked)
                .OrderBy(item => item.LoadingPoint.Code, StringComparer.Ordinal)
                .FirstOrDefault();
            return candidate is null
                ? throw new InvalidOperationException("No loading point with a pallet is available for putaway.")
                : candidate;
        }
    }

    private PutawayLocationCandidate FindRequested(Guid id)
        => _locations.FirstOrDefault(item => item.Location.Id == id)
            ?? throw new KeyNotFoundException($"Location '{id}' was not found.");

    private PutawayLocationCandidate FindRecommended(PutawayAllocationRequest request)
        => _locations
            .Where(candidate => IsEligible(candidate, request))
            .OrderBy(candidate => candidate.Location.Code, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No location satisfies the putaway dimensions, weight and operational constraints.");

    private static void EnsureAvailable(PutawayLocationCandidate candidate, PutawayAllocationRequest request)
    {
        if (!IsEligible(candidate, request))
        {
            throw new InvalidOperationException($"Location '{candidate.Location.Code}' is not available for this putaway.");
        }
    }

    private static bool IsEligible(PutawayLocationCandidate candidate, PutawayAllocationRequest request)
    {
        if (candidate.EffectiveDisabled || candidate.IsFaulted || candidate.EffectiveLocked || candidate.EffectiveOccupied)
        {
            return false;
        }

        if (candidate.OccupiedUnits >= candidate.Location.Capacity)
        {
            return false;
        }

        if (request.LengthMm > candidate.Location.LengthMm
            || request.WidthMm > candidate.Location.WidthMm
            || request.HeightMm > candidate.Location.HeightMm)
        {
            return false;
        }

        return candidate.OccupiedWeightKg + request.WeightKg <= candidate.Location.MaxWeightKg;
    }

    private static void EnsureLoadingPointAvailable(PutawayLoadingPointCandidate candidate)
    {
        if (!candidate.HasPallet)
        {
            throw new InvalidOperationException($"Loading point '{candidate.LoadingPoint.Code}' has no pallet.");
        }

        if (candidate.IsFaulted || candidate.EffectiveDisabled || candidate.EffectiveLocked)
        {
            throw new InvalidOperationException($"Loading point '{candidate.LoadingPoint.Code}' is not available.");
        }
    }

    private static void ValidateDimensions(PutawayAllocationRequest request)
    {
        if (request.PendingInboundInventoryId == Guid.Empty)
        {
            throw new ArgumentException("A pending inbound inventory id is required.", nameof(request));
        }

        if (request.LengthMm <= 0m || request.WidthMm <= 0m || request.HeightMm <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Putaway dimensions must be greater than zero.");
        }

        if (request.WeightKg < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.WeightKg, "Putaway weight cannot be negative.");
        }
    }

    private static string Fingerprint(PutawayAllocationRequest request)
        => string.Join(
            "|",
            request.PendingInboundInventoryId.ToString("D"),
            request.LengthMm.ToString("G29", CultureInfo.InvariantCulture),
            request.WidthMm.ToString("G29", CultureInfo.InvariantCulture),
            request.HeightMm.ToString("G29", CultureInfo.InvariantCulture),
            request.WeightKg.ToString("G29", CultureInfo.InvariantCulture),
            request.RequestedLocationId?.ToString("D") ?? "-");

    private static void EnsureUniqueLocations(IEnumerable<PutawayLocationCandidate> locations)
    {
        var duplicate = locations.GroupBy(item => item.Location.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Location '{duplicate.Key}' was configured more than once.", nameof(locations));
        }
    }

    private static void EnsureUniqueLoadingPoints(IEnumerable<PutawayLoadingPointCandidate> loadingPoints)
    {
        var duplicate = loadingPoints.GroupBy(item => item.LoadingPoint.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Loading point '{duplicate.Key}' was configured more than once.", nameof(loadingPoints));
        }
    }
}
