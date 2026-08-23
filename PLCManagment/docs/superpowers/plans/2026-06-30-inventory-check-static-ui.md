# Inventory Check Static UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a static ASP.NET-hosted operation page for inventory check outbound, task query, and one-click inbound scheduling.

**Architecture:** Add static files under `PLCManagement.API/wwwroot/inventory-check` and enable static file serving in `Program.cs`. The browser uses relative `fetch` calls to the existing inventory check API endpoints and keeps only current task state in memory.

**Tech Stack:** ASP.NET Core static files, HTML, CSS, vanilla JavaScript, existing `api/inventory-check` endpoints.

---

## File Structure

- Create `API/wwwroot/inventory-check/index.html`: operation page markup.
- Create `API/wwwroot/inventory-check/inventory-check.css`: compact industrial dashboard styling.
- Create `API/wwwroot/inventory-check/inventory-check.js`: form handling, API calls, polling, rendering, error handling.
- Modify `API/Program.cs`: call `app.UseDefaultFiles()` and `app.UseStaticFiles()` before routing.
- Modify `API/Tests/InventoryCheckFeature.Tests.ps1`: assert UI files exist and call required endpoints.

### Task 1: Static UI Assets

**Files:**
- Create: `API/wwwroot/inventory-check/index.html`
- Create: `API/wwwroot/inventory-check/inventory-check.css`
- Create: `API/wwwroot/inventory-check/inventory-check.js`

- [ ] **Step 1: Create HTML operation surface**

Create a page with PLC selector, tray range inputs, task query input, task summary, item table, and inbound scheduling button.

- [ ] **Step 2: Create CSS**

Style the page as compact, scan-friendly, and responsive. Use status badges for `Pending`, `Running`, `Succeeded`, `Failed`, `Ready`, `Scheduled`, `Completed`, and `CompletedWithErrors`.

- [ ] **Step 3: Create JavaScript**

Implement:
- `POST /api/inventory-check/outbound-range`
- `GET /api/inventory-check/tasks/{taskId}`
- `POST /api/inventory-check/tasks/{taskId}/schedule-inbound`
- 3 second polling after outbound starts
- stop polling for terminal task statuses
- visible business and network errors

### Task 2: ASP.NET Static File Hosting

**Files:**
- Modify: `API/Program.cs`

- [ ] **Step 1: Enable static files**

Add `app.UseDefaultFiles();` and `app.UseStaticFiles();` before `app.UseSwagger();` so `/inventory-check/` serves `index.html`.

### Task 3: Verification Coverage

**Files:**
- Modify: `API/Tests/InventoryCheckFeature.Tests.ps1`

- [ ] **Step 1: Add UI asset checks**

Assert `index.html`, `inventory-check.css`, and `inventory-check.js` exist.

- [ ] **Step 2: Add endpoint source checks**

Assert JavaScript contains:
- `/api/inventory-check/outbound-range`
- `/api/inventory-check/tasks/`
- `/schedule-inbound`
- `setInterval`

- [ ] **Step 3: Run verification**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\API\Tests\InventoryCheckFeature.Tests.ps1
dotnet build .\API\PLCManagement.API.csproj --no-restore
```

Expected:
- feature test prints `Inventory check feature checks passed.`
- API build succeeds with 0 errors

## Self-Review

- Spec coverage: hosting, layout, API calls, polling, error handling, visual style, and tests are covered.
- Placeholder scan: no `TBD`, `TODO`, or undefined implementation steps.
- Type consistency: JavaScript uses API response fields defined by existing DTOs: `taskId`, `taskNo`, `plcid`, `trayStart`, `trayEnd`, `status`, `totalCount`, `successCount`, `failedCount`, `message`, and `items`.
