# Inventory Check Outbound Range Design

## Goal

Build an ASP.NET API feature for inventory checking. An operator enters a warehouse PLC number (`A1` through `A8`) and a `LocationManagements.Tray` range such as `A10-701` through `A10-733`. The system finds matching occupied locations, automatically sends them outbound one by one, records the outbound result for each tray, and allows successfully outbound trays to be scheduled for inbound with one action.

## Existing Context

The API project is `PLCManagment/API`.

Existing behavior to reuse:

- `PlcOperationsController` already exposes single outbound and inbound operations.
- `IPlcService.OutboundOperation(plcId, shelf, position, loadingPoint)` validates loading point state, location state, PLC state, then writes the outbound registers.
- `IPlcService.InboundOperation(plcId, shelf, position, loadingPoint)` can execute inbound immediately or register a background inbound task through `SDL_PreUPLoadTray` when the PLC is busy.
- `LocationManagements` contains `PLCID`, `Shelf`, `Position`, `Tray`, and `ShelfStatus`.
- `LocationOperationLogs` records low-level inbound/outbound actions, but it does not represent a whole inventory checking batch.

## Recommended Approach

Add a dedicated inventory-check task layer in the API:

- `InventoryCheckController` exposes task creation, task detail, and one-click inbound scheduling endpoints.
- `InventoryCheckService` creates the task, expands the tray range, queues items, and drives background outbound execution.
- New task tables/models store batch-level and item-level state, separate from `LocationOperationLogs`.

This keeps existing PLC command logic in one place while adding the missing business workflow around it.

## API

Create outbound task:

```http
POST /api/inventory-check/outbound-range
```

Request:

```json
{
  "plcId": "A1",
  "trayStart": "A10-701",
  "trayEnd": "A10-733"
}
```

Response includes `taskId`, total matched count, and initial item list.

Query task:

```http
GET /api/inventory-check/tasks/{taskId}
```

Returns batch status and item statuses.

One-click schedule inbound:

```http
POST /api/inventory-check/tasks/{taskId}/schedule-inbound
```

This schedules inbound only for items that were successfully outbound and have not already been scheduled inbound.

## Range Rules

The input range is based on `LocationManagements.Tray`.

Supported format:

```text
<prefix>-<number>
```

Examples:

```text
A10-701
A10-733
```

Rules:

- Both endpoints must have the same prefix, for example `A10`.
- The numeric suffix is compared as an integer.
- The service queries only rows where `PLCID` matches the requested PLC.
- The service includes only occupied locations by default: `ShelfStatus = 1` and `Tray` is not empty.
- Items are sorted by numeric suffix ascending.

Invalid examples:

- Different prefixes: `A10-701` to `A11-733`.
- Missing numeric suffix.
- Start number greater than end number.
- PLC outside `A1` through `A8`.

## Data Model

Add `InventoryCheckTask`:

- `Id`
- `TaskNo`
- `PLCID`
- `TrayStart`
- `TrayEnd`
- `Status`
- `TotalCount`
- `SuccessCount`
- `FailedCount`
- `CreatedAt`
- `StartedAt`
- `CompletedAt`
- `Message`

Add `InventoryCheckItem`:

- `Id`
- `TaskId`
- `PLCID`
- `Tray`
- `Shelf`
- `Position`
- `OriginalShelfStatus`
- `OutboundLoadingPoint`
- `OutboundStatus`
- `OutboundOperationLogId`
- `OutboundStartedAt`
- `OutboundCompletedAt`
- `OutboundMessage`
- `InboundStatus`
- `InboundLoadingPoint`
- `InboundScheduledAt`
- `InboundMessage`

Suggested statuses:

- Task: `Pending`, `Running`, `Completed`, `CompletedWithErrors`, `Failed`, `Canceled`.
- Outbound item: `Pending`, `Running`, `Succeeded`, `Failed`, `Skipped`.
- Inbound item: `NotReady`, `Ready`, `Scheduled`, `Failed`.

## Outbound Flow

1. Validate request.
2. Parse `trayStart` and `trayEnd`.
3. Query `LocationManagements` for matching `PLCID`, occupied status, non-empty `Tray`, matching prefix, and numeric suffix inside the range.
4. Create one `InventoryCheckTask` and one `InventoryCheckItem` per matching location.
5. Start background processing and return immediately.
6. The background worker processes one item at a time for that PLC.
7. Before each item, check PLC state and choose an empty loading point from `0` and `1`.
8. Call `OutboundOperation(plcId, shelf, position, loadingPoint)`.
9. Record success or failure on the item. If successful, mark the item inbound status as `Ready`.
10. Continue until all items are handled.
11. Mark the task `Completed` or `CompletedWithErrors`.

## Loading Point Selection

The system automatically chooses an empty loading point:

1. Prefer loading point `0` if empty.
2. Otherwise use loading point `1` if empty.
3. If neither is empty, wait and retry for a bounded interval.
4. If no empty point becomes available, mark the item failed with a clear message.

The design assumes the existing loading-point checks are authoritative.

## One-Click Inbound Scheduling

The schedule endpoint finds items in the task with:

- `OutboundStatus = Succeeded`
- `InboundStatus = Ready` or `Failed`

For each item, it attempts to schedule the tray back to the original `Shelf` and `Position`.

The preferred path is to reuse `IPlcService.InboundOperation(plcId, shelf, position, loadingPoint)` because it already validates the target location, checks loading-point state, and registers background inbound through `SDL_PreUPLoadTray` when the PLC is busy.

Inbound loading point selection uses the loading point recorded during outbound first, because that is where the tray should have been placed. If that point is no longer suitable, the item is marked failed with a message instead of guessing.

## Error Handling

Validation errors return `400`.

No matching rows returns `404` with a message that no occupied trays were found in the range for the PLC.

Per-item failures do not fail the whole task immediately. The task continues with the next item and ends as `CompletedWithErrors`.

The service records messages for:

- Invalid tray range.
- PLC not found or offline.
- PLC busy timeout.
- Fork has goods.
- No empty loading point.
- Source location no longer has goods.
- Outbound command failure.
- Inbound scheduling failure.

## Concurrency

Only one inventory-check outbound worker should execute for the same PLC at a time. If a task is already running for `A1`, creating another running task for `A1` should return a conflict response.

Tasks for different PLCs may run independently.

## Testing

Follow the repository's current lightweight regression style under `PLCManagment/API/Tests`.

Add tests that check:

- Tray range parsing requires matching prefix and numeric suffixes.
- The query is based on `LocationManagements.Tray`, not `Shelf` or `Position`.
- The outbound-range controller exists and exposes the expected route.
- The service stores original `Shelf`, `Position`, and `Tray` on each item.
- Loading point selection checks both `0` and `1`.
- One-click inbound scheduling only targets successfully outbound items.
- `Program.cs` registers the new service.

Also run `dotnet build` for the API solution.

## Open Decisions

None. The current decisions are:

- Tray range comes from `LocationManagements.Tray`.
- Loading point is automatically selected.
- The implementation adds dedicated inventory-check task records.
- Inbound scheduling reuses existing inbound/background pre-upload behavior.
