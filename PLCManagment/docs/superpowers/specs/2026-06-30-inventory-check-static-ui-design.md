# Inventory Check Static UI Design

## Goal

Add a lightweight ASP.NET-hosted static operation page for inventory check work. The page lets an operator start automatic outbound by PLC and tray range, query task progress, and schedule inbound for trays that are ready after outbound.

## Hosting

The UI will live inside the existing `PLCManagement.API` project as static files:

- `wwwroot/inventory-check/index.html`
- `wwwroot/inventory-check/inventory-check.css`
- `wwwroot/inventory-check/inventory-check.js`

`Program.cs` will enable static file serving. Operators will open `/inventory-check/` on the same host as the API, so the page can call relative API URLs without extra CORS or deployment work.

## Page Layout

The first screen is the actual operation surface, not a landing page.

The page has four working areas:

1. Outbound form
   - PLC selector with `A1` through `A8`
   - tray start input, for example `A10-701`
   - tray end input, for example `A10-733`
   - start outbound button

2. Task query
   - task id input
   - query button
   - current task number, PLC, tray range, status, total count, success count, failed count, and message

3. Item table
   - tray
   - shelf
   - position
   - outbound loading point
   - outbound status and message
   - inbound status and message

4. Inbound action
   - one-click schedule inbound button
   - enabled only when a task has been loaded
   - calls the existing inbound scheduling endpoint for the current task

## API Calls

The UI uses the inventory check API that already exists:

- `POST /api/inventory-check/outbound-range`
  - body: `{ plcId, trayStart, trayEnd }`
  - starts the inventory check outbound task
  - stores the returned `taskId` as the current task

- `GET /api/inventory-check/tasks/{taskId}`
  - refreshes task status and item rows

- `POST /api/inventory-check/tasks/{taskId}/schedule-inbound`
  - schedules inbound for items that are outbound-succeeded and ready

## State and Polling

After outbound starts, the UI polls the task every 3 seconds. Polling stops when task status is one of:

- `Completed`
- `CompletedWithErrors`
- `Failed`
- `Canceled`

The operator can also query manually by task id. The page keeps only in-browser state and does not add new backend tables.

## Error Handling

The UI validates required fields before calling the API. API failures are shown in a visible message area. If an API response includes a business message, that message is displayed to the operator. Network errors show a short failure message and keep the current screen state.

## Visual Style

The interface should be compact and work-focused:

- restrained colors with clear status badges
- dense table layout for repeated scanning
- stable button widths and input sizes
- responsive layout that remains usable on a laptop or shop-floor tablet

## Testing

Verification will include:

- static file presence checks for the new UI assets
- source checks that the page calls the three required API endpoints
- `dotnet build .\API\PLCManagement.API.csproj --no-restore`

Manual browser testing can be done by running the API and opening `/inventory-check/`.
