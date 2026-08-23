# Inventory Check Outbound Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add ASP.NET API support for inventory-check range outbound by `LocationManagements.Tray`, with automatic loading-point selection, per-tray records, and one-click inbound scheduling.

**Architecture:** Add a focused inventory-check feature slice to `PLCManagment/API`: DTOs, EF entities, `IInventoryCheckService`, `InventoryCheckService`, and `InventoryCheckController`. The service creates durable task/item records, runs outbound in a bounded background worker per PLC, reuses `IPlcService.OutboundOperation`, and schedules inbound through existing `IPlcService.InboundOperation`.

**Tech Stack:** ASP.NET Core 8, Entity Framework Core SQL Server, existing PowerShell static regression tests, existing `IPlcService` and `ILocationCheckService`.

---

## File Structure

- Create `PLCManagment/API/Models/InventoryCheckTask.cs`: EF entity for one inventory-check batch.
- Create `PLCManagment/API/Models/InventoryCheckItem.cs`: EF entity for one tray in a batch.
- Create `PLCManagment/API/Models/Dtos/InventoryCheckDto.cs`: request/response DTOs and status constants.
- Create `PLCManagment/API/Interfaces/IInventoryCheckService.cs`: service interface.
- Create `PLCManagment/API/Services/InventoryCheckService.cs`: validation, range expansion, background outbound, loading-point selection, inbound scheduling.
- Create `PLCManagment/API/Controllers/InventoryCheckController.cs`: HTTP endpoints.
- Modify `PLCManagment/API/Data/ApplicationDbContext.cs`: add DbSets and entity configuration.
- Modify `PLCManagment/API/Program.cs`: register `IInventoryCheckService`.
- Create EF migration under `PLCManagment/API/Migrations`: adds `InventoryCheckTasks` and `InventoryCheckItems`.
- Create `PLCManagment/API/Tests/InventoryCheckFeature.Tests.ps1`: static regression checks.

Because this workspace is not a Git repository, replace commit steps with a checkpoint command that lists changed files.

---

### Task 1: Regression Test Scaffold

**Files:**
- Create: `PLCManagment/API/Tests/InventoryCheckFeature.Tests.ps1`

- [ ] **Step 1: Write the failing static regression test**

Create `PLCManagment/API/Tests/InventoryCheckFeature.Tests.ps1`:

```powershell
$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot '..'
$controllerPath = Join-Path $root 'Controllers\InventoryCheckController.cs'
$servicePath = Join-Path $root 'Services\InventoryCheckService.cs'
$interfacePath = Join-Path $root 'Interfaces\IInventoryCheckService.cs'
$dtoPath = Join-Path $root 'Models\Dtos\InventoryCheckDto.cs'
$taskModelPath = Join-Path $root 'Models\InventoryCheckTask.cs'
$itemModelPath = Join-Path $root 'Models\InventoryCheckItem.cs'
$dbContextPath = Join-Path $root 'Data\ApplicationDbContext.cs'
$programPath = Join-Path $root 'Program.cs'

function Assert-FileExists($Path, $Message) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw $Message
    }
}

function Assert-Contains($Text, $Pattern, $Message) {
    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

Assert-FileExists $controllerPath 'InventoryCheckController must exist.'
Assert-FileExists $servicePath 'InventoryCheckService must exist.'
Assert-FileExists $interfacePath 'IInventoryCheckService must exist.'
Assert-FileExists $dtoPath 'Inventory check DTOs must exist.'
Assert-FileExists $taskModelPath 'InventoryCheckTask model must exist.'
Assert-FileExists $itemModelPath 'InventoryCheckItem model must exist.'

$controller = Get-Content -Raw -Path $controllerPath
$service = Get-Content -Raw -Path $servicePath
$dbContext = Get-Content -Raw -Path $dbContextPath
$program = Get-Content -Raw -Path $programPath

Assert-Contains $controller '\[Route\("api/inventory-check"\)\]' `
    'InventoryCheckController route must be api/inventory-check.'
Assert-Contains $controller 'outbound-range' `
    'Controller must expose outbound-range endpoint.'
Assert-Contains $controller 'schedule-inbound' `
    'Controller must expose one-click inbound scheduling endpoint.'

Assert-Contains $service 'ParseTrayRange' `
    'Service must parse tray ranges explicitly.'
Assert-Contains $service 'LocationManagements' `
    'Service must query LocationManagements.'
Assert-Contains $service '\.Tray' `
    'Service must base range selection on LocationManagements.Tray.'
Assert-Contains $service 'IsLoadingPointEmpty\(request\.PlcId, 0\)|IsLoadingPointEmpty\(plcId, 0\)' `
    'Service must check loading point 0.'
Assert-Contains $service 'IsLoadingPointEmpty\(request\.PlcId, 1\)|IsLoadingPointEmpty\(plcId, 1\)' `
    'Service must check loading point 1.'
Assert-Contains $service 'OutboundOperation\(' `
    'Service must reuse existing outbound operation.'
Assert-Contains $service 'InboundOperation\(' `
    'Service must reuse existing inbound operation.'

Assert-Contains $dbContext 'DbSet<InventoryCheckTask>' `
    'DbContext must expose InventoryCheckTasks.'
Assert-Contains $dbContext 'DbSet<InventoryCheckItem>' `
    'DbContext must expose InventoryCheckItems.'
Assert-Contains $program 'AddScoped<IInventoryCheckService, InventoryCheckService>' `
    'Program.cs must register inventory check service.'

Write-Host 'Inventory check feature checks passed.'
```

- [ ] **Step 2: Run the test to verify it fails**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\PLCManagment\API\Tests\InventoryCheckFeature.Tests.ps1
```

Expected: FAIL with `InventoryCheckController must exist.`

- [ ] **Step 3: Checkpoint**

Run:

```powershell
Get-ChildItem -LiteralPath .\PLCManagment\API\Tests\InventoryCheckFeature.Tests.ps1 | Select-Object FullName,Length
```

Expected: the new test file is listed.

---

### Task 2: DTOs, Entities, and DbContext

**Files:**
- Create: `PLCManagment/API/Models/Dtos/InventoryCheckDto.cs`
- Create: `PLCManagment/API/Models/InventoryCheckTask.cs`
- Create: `PLCManagment/API/Models/InventoryCheckItem.cs`
- Modify: `PLCManagment/API/Data/ApplicationDbContext.cs`

- [ ] **Step 1: Add DTOs and status constants**

Create `PLCManagment/API/Models/Dtos/InventoryCheckDto.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace PLCManagement.API.Models.Dtos
{
    public static class InventoryCheckStatuses
    {
        public const string Pending = "Pending";
        public const string Running = "Running";
        public const string Completed = "Completed";
        public const string CompletedWithErrors = "CompletedWithErrors";
        public const string Failed = "Failed";
        public const string Canceled = "Canceled";
        public const string Succeeded = "Succeeded";
        public const string Skipped = "Skipped";
        public const string NotReady = "NotReady";
        public const string Ready = "Ready";
        public const string Scheduled = "Scheduled";
    }

    public class InventoryCheckOutboundRangeRequest
    {
        [Required]
        public string PlcId { get; set; } = string.Empty;

        [Required]
        public string TrayStart { get; set; } = string.Empty;

        [Required]
        public string TrayEnd { get; set; } = string.Empty;
    }

    public class InventoryCheckTaskResponse
    {
        public long TaskId { get; set; }
        public string TaskNo { get; set; } = string.Empty;
        public string PLCID { get; set; } = string.Empty;
        public string TrayStart { get; set; } = string.Empty;
        public string TrayEnd { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int TotalCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<InventoryCheckItemResponse> Items { get; set; } = new();
    }

    public class InventoryCheckItemResponse
    {
        public long Id { get; set; }
        public string PLCID { get; set; } = string.Empty;
        public string Tray { get; set; } = string.Empty;
        public int Shelf { get; set; }
        public int Position { get; set; }
        public int? OutboundLoadingPoint { get; set; }
        public string OutboundStatus { get; set; } = string.Empty;
        public string OutboundMessage { get; set; } = string.Empty;
        public string InboundStatus { get; set; } = string.Empty;
        public int? InboundLoadingPoint { get; set; }
        public string InboundMessage { get; set; } = string.Empty;
    }
}
```

- [ ] **Step 2: Add EF entities**

Create `PLCManagment/API/Models/InventoryCheckTask.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace PLCManagement.API.Models
{
    public class InventoryCheckTask
    {
        [Key]
        public long Id { get; set; }
        public string TaskNo { get; set; } = string.Empty;
        public string PLCID { get; set; } = string.Empty;
        public string TrayStart { get; set; } = string.Empty;
        public string TrayEnd { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int TotalCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string Message { get; set; } = string.Empty;
        public ICollection<InventoryCheckItem> Items { get; set; } = new List<InventoryCheckItem>();
    }
}
```

Create `PLCManagment/API/Models/InventoryCheckItem.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace PLCManagement.API.Models
{
    public class InventoryCheckItem
    {
        [Key]
        public long Id { get; set; }
        public long TaskId { get; set; }
        public InventoryCheckTask? Task { get; set; }
        public string PLCID { get; set; } = string.Empty;
        public string Tray { get; set; } = string.Empty;
        public int Shelf { get; set; }
        public int Position { get; set; }
        public int OriginalShelfStatus { get; set; }
        public int? OutboundLoadingPoint { get; set; }
        public string OutboundStatus { get; set; } = string.Empty;
        public long? OutboundOperationLogId { get; set; }
        public DateTime? OutboundStartedAt { get; set; }
        public DateTime? OutboundCompletedAt { get; set; }
        public string OutboundMessage { get; set; } = string.Empty;
        public string InboundStatus { get; set; } = string.Empty;
        public int? InboundLoadingPoint { get; set; }
        public DateTime? InboundScheduledAt { get; set; }
        public string InboundMessage { get; set; } = string.Empty;
    }
}
```

- [ ] **Step 3: Modify DbContext**

In `PLCManagment/API/Data/ApplicationDbContext.cs`, add:

```csharp
public DbSet<InventoryCheckTask> InventoryCheckTasks { get; set; }
public DbSet<InventoryCheckItem> InventoryCheckItems { get; set; }
```

Inside `OnModelCreating`, add:

```csharp
modelBuilder.Entity<InventoryCheckTask>(entity =>
{
    entity.HasIndex(e => e.TaskNo).IsUnique();
    entity.Property(e => e.TaskNo).HasMaxLength(50);
    entity.Property(e => e.PLCID).HasMaxLength(10);
    entity.Property(e => e.TrayStart).HasMaxLength(50);
    entity.Property(e => e.TrayEnd).HasMaxLength(50);
    entity.Property(e => e.Status).HasMaxLength(50);
    entity.Property(e => e.Message).HasMaxLength(500);
});

modelBuilder.Entity<InventoryCheckItem>(entity =>
{
    entity.HasIndex(e => new { e.TaskId, e.Tray }).IsUnique();
    entity.Property(e => e.PLCID).HasMaxLength(10);
    entity.Property(e => e.Tray).HasMaxLength(50);
    entity.Property(e => e.OutboundStatus).HasMaxLength(50);
    entity.Property(e => e.OutboundMessage).HasMaxLength(500);
    entity.Property(e => e.InboundStatus).HasMaxLength(50);
    entity.Property(e => e.InboundMessage).HasMaxLength(500);
    entity.HasOne(e => e.Task)
        .WithMany(e => e.Items)
        .HasForeignKey(e => e.TaskId)
        .OnDelete(DeleteBehavior.Cascade);
});
```

- [ ] **Step 4: Run static test**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\PLCManagment\API\Tests\InventoryCheckFeature.Tests.ps1
```

Expected: still FAIL because controller/service/interface do not exist yet.

---

### Task 3: Service Interface and Implementation

**Files:**
- Create: `PLCManagment/API/Interfaces/IInventoryCheckService.cs`
- Create: `PLCManagment/API/Services/InventoryCheckService.cs`

- [ ] **Step 1: Add service interface**

Create `PLCManagment/API/Interfaces/IInventoryCheckService.cs`:

```csharp
using PLCManagement.API.Models.Dtos;

namespace PLCManagement.API.Interfaces
{
    public interface IInventoryCheckService
    {
        Task<InventoryCheckTaskResponse> CreateOutboundRangeTaskAsync(InventoryCheckOutboundRangeRequest request);
        Task<InventoryCheckTaskResponse?> GetTaskAsync(long taskId);
        Task<InventoryCheckTaskResponse> ScheduleInboundAsync(long taskId);
    }
}
```

- [ ] **Step 2: Add service implementation**

Create `PLCManagment/API/Services/InventoryCheckService.cs` with these core members:

```csharp
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PLCManagement.API.Data;
using PLCManagement.API.Interfaces;
using PLCManagement.API.Models;
using PLCManagement.API.Models.Dtos;

namespace PLCManagement.API.Services
{
    public class InventoryCheckService : IInventoryCheckService
    {
        private static readonly Regex TrayRegex = new(@"^(?<prefix>.+)-(?<number>\d+)$", RegexOptions.Compiled);
        private static readonly ConcurrentDictionary<string, byte> RunningPlcs = new(StringComparer.OrdinalIgnoreCase);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<InventoryCheckService> _logger;

        public InventoryCheckService(IServiceScopeFactory scopeFactory, ILogger<InventoryCheckService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<InventoryCheckTaskResponse> CreateOutboundRangeTaskAsync(InventoryCheckOutboundRangeRequest request)
        {
            var range = ParseTrayRange(request.TrayStart, request.TrayEnd);
            var plcId = NormalizePlcId(request.PlcId);

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var hasRunningTask = await context.InventoryCheckTasks.AnyAsync(t =>
                t.PLCID == plcId &&
                (t.Status == InventoryCheckStatuses.Pending || t.Status == InventoryCheckStatuses.Running));
            if (hasRunningTask)
            {
                throw new InvalidOperationException($"PLC {plcId} 已有盘点下架任务正在执行。");
            }

            var candidates = await context.LocationManagements
                .Where(l => l.PLCID == plcId && l.ShelfStatus == 1 && l.Tray != null && l.Tray.StartsWith(range.Prefix + "-"))
                .ToListAsync();

            var matched = candidates
                .Select(l => new { Location = l, Parsed = TryParseTray(l.Tray ?? string.Empty) })
                .Where(x => x.Parsed != null && x.Parsed.Value.Prefix == range.Prefix && x.Parsed.Value.Number >= range.Start && x.Parsed.Value.Number <= range.End)
                .OrderBy(x => x.Parsed!.Value.Number)
                .ToList();

            if (!matched.Any())
            {
                throw new KeyNotFoundException($"PLC {plcId} 在 {request.TrayStart} 至 {request.TrayEnd} 范围内没有找到有货储位。");
            }

            var now = DateTime.Now;
            var task = new InventoryCheckTask
            {
                TaskNo = $"IC{now:yyyyMMddHHmmssfff}",
                PLCID = plcId,
                TrayStart = request.TrayStart.Trim(),
                TrayEnd = request.TrayEnd.Trim(),
                Status = InventoryCheckStatuses.Pending,
                TotalCount = matched.Count,
                CreatedAt = now,
                Message = "盘点下架任务已创建"
            };

            foreach (var item in matched)
            {
                task.Items.Add(new InventoryCheckItem
                {
                    PLCID = plcId,
                    Tray = item.Location.Tray ?? string.Empty,
                    Shelf = item.Location.Shelf,
                    Position = item.Location.Position,
                    OriginalShelfStatus = item.Location.ShelfStatus,
                    OutboundStatus = InventoryCheckStatuses.Pending,
                    InboundStatus = InventoryCheckStatuses.NotReady
                });
            }

            context.InventoryCheckTasks.Add(task);
            await context.SaveChangesAsync();

            _ = Task.Run(() => ProcessOutboundTaskAsync(task.Id));

            return MapTask(task);
        }

        public async Task<InventoryCheckTaskResponse?> GetTaskAsync(long taskId)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var task = await context.InventoryCheckTasks
                .Include(t => t.Items.OrderBy(i => i.Id))
                .FirstOrDefaultAsync(t => t.Id == taskId);
            return task == null ? null : MapTask(task);
        }

        public async Task<InventoryCheckTaskResponse> ScheduleInboundAsync(long taskId)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var plcService = scope.ServiceProvider.GetRequiredService<IPlcService>();
            var task = await context.InventoryCheckTasks
                .Include(t => t.Items)
                .FirstOrDefaultAsync(t => t.Id == taskId);

            if (task == null)
            {
                throw new KeyNotFoundException($"盘点任务 {taskId} 不存在。");
            }

            foreach (var item in task.Items.Where(i =>
                i.OutboundStatus == InventoryCheckStatuses.Succeeded &&
                (i.InboundStatus == InventoryCheckStatuses.Ready || i.InboundStatus == InventoryCheckStatuses.Failed)))
            {
                if (!item.OutboundLoadingPoint.HasValue)
                {
                    item.InboundStatus = InventoryCheckStatuses.Failed;
                    item.InboundMessage = "缺少下架装载点，不能预约上架。";
                    continue;
                }

                var result = await plcService.InboundOperation(item.PLCID, item.Shelf, item.Position, item.OutboundLoadingPoint.Value);
                item.InboundLoadingPoint = item.OutboundLoadingPoint.Value;
                item.InboundScheduledAt = DateTime.Now;
                item.InboundStatus = result.IsSuccess ? InventoryCheckStatuses.Scheduled : InventoryCheckStatuses.Failed;
                item.InboundMessage = result.Message;
            }

            await context.SaveChangesAsync();
            return MapTask(task);
        }

        private async Task ProcessOutboundTaskAsync(long taskId)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var plcService = scope.ServiceProvider.GetRequiredService<IPlcService>();
            var locationCheckService = scope.ServiceProvider.GetRequiredService<ILocationCheckService>();

            var task = await context.InventoryCheckTasks.Include(t => t.Items).FirstOrDefaultAsync(t => t.Id == taskId);
            if (task == null || !RunningPlcs.TryAdd(task.PLCID, 0))
            {
                return;
            }

            try
            {
                task.Status = InventoryCheckStatuses.Running;
                task.StartedAt = DateTime.Now;
                await context.SaveChangesAsync();

                foreach (var item in task.Items.OrderBy(i => i.Id))
                {
                    item.OutboundStatus = InventoryCheckStatuses.Running;
                    item.OutboundStartedAt = DateTime.Now;
                    await context.SaveChangesAsync();

                    var loadingPoint = await ChooseEmptyLoadingPoint(task.PLCID, locationCheckService);
                    if (!loadingPoint.HasValue)
                    {
                        item.OutboundStatus = InventoryCheckStatuses.Failed;
                        item.OutboundCompletedAt = DateTime.Now;
                        item.OutboundMessage = "装载点0和1均未空闲，跳过该货框。";
                        continue;
                    }

                    item.OutboundLoadingPoint = loadingPoint.Value;
                    var result = await plcService.OutboundOperation(item.PLCID, item.Shelf, item.Position, loadingPoint.Value);
                    item.OutboundCompletedAt = DateTime.Now;
                    item.OutboundStatus = result.IsSuccess ? InventoryCheckStatuses.Succeeded : InventoryCheckStatuses.Failed;
                    item.InboundStatus = result.IsSuccess ? InventoryCheckStatuses.Ready : InventoryCheckStatuses.NotReady;
                    item.OutboundMessage = result.Message;
                    await context.SaveChangesAsync();
                }

                task.SuccessCount = task.Items.Count(i => i.OutboundStatus == InventoryCheckStatuses.Succeeded);
                task.FailedCount = task.Items.Count(i => i.OutboundStatus == InventoryCheckStatuses.Failed);
                task.Status = task.FailedCount == 0 ? InventoryCheckStatuses.Completed : InventoryCheckStatuses.CompletedWithErrors;
                task.CompletedAt = DateTime.Now;
                task.Message = $"盘点下架完成，成功 {task.SuccessCount} 个，失败 {task.FailedCount} 个。";
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "盘点下架任务执行异常: TaskId={TaskId}", taskId);
                task.Status = InventoryCheckStatuses.Failed;
                task.CompletedAt = DateTime.Now;
                task.Message = ex.Message;
                await context.SaveChangesAsync();
            }
            finally
            {
                RunningPlcs.TryRemove(task.PLCID, out _);
            }
        }

        private static async Task<int?> ChooseEmptyLoadingPoint(string plcId, ILocationCheckService locationCheckService)
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (await locationCheckService.IsLoadingPointEmpty(plcId, 0))
                {
                    return 0;
                }

                if (await locationCheckService.IsLoadingPointEmpty(plcId, 1))
                {
                    return 1;
                }

                await Task.Delay(1000);
            }

            return null;
        }

        private static string NormalizePlcId(string plcId)
        {
            var value = (plcId ?? string.Empty).Trim().ToUpperInvariant();
            if (!Regex.IsMatch(value, "^A[1-8]$"))
            {
                throw new ArgumentException("立库编号必须为 A1 至 A8。");
            }
            return value;
        }

        private static (string Prefix, int Start, int End) ParseTrayRange(string trayStart, string trayEnd)
        {
            var start = TryParseTray(trayStart);
            var end = TryParseTray(trayEnd);
            if (start == null || end == null)
            {
                throw new ArgumentException("储位编号格式必须为 前缀-数字，例如 A10-701。");
            }

            if (!string.Equals(start.Value.Prefix, end.Value.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("储位编号范围前缀必须一致。");
            }

            if (start.Value.Number > end.Value.Number)
            {
                throw new ArgumentException("储位编号起始值不能大于结束值。");
            }

            return (start.Value.Prefix, start.Value.Number, end.Value.Number);
        }

        private static (string Prefix, int Number)? TryParseTray(string tray)
        {
            var match = TrayRegex.Match((tray ?? string.Empty).Trim());
            if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var number))
            {
                return null;
            }
            return (match.Groups["prefix"].Value.ToUpperInvariant(), number);
        }

        private static InventoryCheckTaskResponse MapTask(InventoryCheckTask task)
        {
            return new InventoryCheckTaskResponse
            {
                TaskId = task.Id,
                TaskNo = task.TaskNo,
                PLCID = task.PLCID,
                TrayStart = task.TrayStart,
                TrayEnd = task.TrayEnd,
                Status = task.Status,
                TotalCount = task.TotalCount,
                SuccessCount = task.SuccessCount,
                FailedCount = task.FailedCount,
                Message = task.Message,
                Items = task.Items.OrderBy(i => i.Id).Select(i => new InventoryCheckItemResponse
                {
                    Id = i.Id,
                    PLCID = i.PLCID,
                    Tray = i.Tray,
                    Shelf = i.Shelf,
                    Position = i.Position,
                    OutboundLoadingPoint = i.OutboundLoadingPoint,
                    OutboundStatus = i.OutboundStatus,
                    OutboundMessage = i.OutboundMessage,
                    InboundStatus = i.InboundStatus,
                    InboundLoadingPoint = i.InboundLoadingPoint,
                    InboundMessage = i.InboundMessage
                }).ToList()
            };
        }
    }
}
```

- [ ] **Step 3: Run static test**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\PLCManagment\API\Tests\InventoryCheckFeature.Tests.ps1
```

Expected: still FAIL because controller and registration do not exist yet.

---

### Task 4: Controller and Dependency Registration

**Files:**
- Create: `PLCManagment/API/Controllers/InventoryCheckController.cs`
- Modify: `PLCManagment/API/Program.cs`

- [ ] **Step 1: Add controller**

Create `PLCManagment/API/Controllers/InventoryCheckController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using PLCManagement.API.Interfaces;
using PLCManagement.API.Models.Dtos;

namespace PLCManagement.API.Controllers
{
    [ApiController]
    [Route("api/inventory-check")]
    public class InventoryCheckController : ControllerBase
    {
        private readonly IInventoryCheckService _inventoryCheckService;
        private readonly ILogger<InventoryCheckController> _logger;

        public InventoryCheckController(
            IInventoryCheckService inventoryCheckService,
            ILogger<InventoryCheckController> logger)
        {
            _inventoryCheckService = inventoryCheckService;
            _logger = logger;
        }

        [HttpPost("outbound-range")]
        public async Task<IActionResult> CreateOutboundRangeTask([FromBody] InventoryCheckOutboundRangeRequest request)
        {
            try
            {
                var result = await _inventoryCheckService.CreateOutboundRangeTaskAsync(request);
                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new ApiResponse<string> { Success = false, Message = ex.Message, Data = null });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new ApiResponse<string> { Success = false, Message = ex.Message, Data = null });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ApiResponse<string> { Success = false, Message = ex.Message, Data = null });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建盘点下架任务失败");
                return StatusCode(500, new ApiResponse<string> { Success = false, Message = "创建盘点下架任务失败", Data = ex.Message });
            }
        }

        [HttpGet("tasks/{taskId:long}")]
        public async Task<IActionResult> GetTask(long taskId)
        {
            var result = await _inventoryCheckService.GetTaskAsync(taskId);
            if (result == null)
            {
                return NotFound(new ApiResponse<string> { Success = false, Message = $"盘点任务 {taskId} 不存在", Data = null });
            }

            return Ok(result);
        }

        [HttpPost("tasks/{taskId:long}/schedule-inbound")]
        public async Task<IActionResult> ScheduleInbound(long taskId)
        {
            try
            {
                var result = await _inventoryCheckService.ScheduleInboundAsync(taskId);
                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ApiResponse<string> { Success = false, Message = ex.Message, Data = null });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "预约盘点上架失败: TaskId={TaskId}", taskId);
                return StatusCode(500, new ApiResponse<string> { Success = false, Message = "预约盘点上架失败", Data = ex.Message });
            }
        }
    }
}
```

- [ ] **Step 2: Register service**

In `PLCManagment/API/Program.cs`, add after the existing scoped services:

```csharp
builder.Services.AddScoped<IInventoryCheckService, InventoryCheckService>();
```

- [ ] **Step 3: Run static test**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\PLCManagment\API\Tests\InventoryCheckFeature.Tests.ps1
```

Expected: PASS with `Inventory check feature checks passed.`

---

### Task 5: EF Migration and Build Verification

**Files:**
- Create: `PLCManagment/API/Migrations/<timestamp>_AddInventoryCheckTasks.cs`
- Create/modify: `PLCManagment/API/Migrations/ApplicationDbContextModelSnapshot.cs`

- [ ] **Step 1: Generate migration**

Run:

```powershell
dotnet ef migrations add AddInventoryCheckTasks --project .\PLCManagment\API\PLCManagement.API.csproj --startup-project .\PLCManagment\API\PLCManagement.API.csproj
```

Expected: migration files are generated under `PLCManagment/API/Migrations`.

- [ ] **Step 2: If `dotnet ef` is unavailable, install or use bundled tool**

If the command fails because `dotnet-ef` is missing, run:

```powershell
dotnet tool install --global dotnet-ef
```

Then rerun Step 1. If network is blocked, request permission for the install.

- [ ] **Step 3: Run tests**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\PLCManagment\API\Tests\InventoryCheckFeature.Tests.ps1
```

Expected: PASS.

- [ ] **Step 4: Build API**

Run:

```powershell
dotnet build .\PLCManagment\API\PLCManagement.API.csproj
```

Expected: build succeeds with 0 errors.

- [ ] **Step 5: Checkpoint changed files**

Run:

```powershell
Get-ChildItem -Recurse .\PLCManagment\API\Controllers\InventoryCheckController.cs,.\PLCManagment\API\Services\InventoryCheckService.cs,.\PLCManagment\API\Interfaces\IInventoryCheckService.cs,.\PLCManagment\API\Models\InventoryCheckTask.cs,.\PLCManagment\API\Models\InventoryCheckItem.cs,.\PLCManagment\API\Models\Dtos\InventoryCheckDto.cs,.\PLCManagment\API\Tests\InventoryCheckFeature.Tests.ps1 | Select-Object FullName,Length
```

Expected: all new feature files are listed.

---

## Self-Review

Spec coverage:

- Tray range comes from `LocationManagements.Tray`: covered in Task 3.
- Automatic loading point selection: covered in Task 3 `ChooseEmptyLoadingPoint`.
- Dedicated task and item records: covered in Task 2.
- Outbound range endpoint: covered in Task 4.
- Task query endpoint: covered in Task 4.
- One-click inbound scheduling: covered in Task 3 and Task 4.
- Existing PLC operations reused: covered in Task 3.
- Tests and build: covered in Task 1 and Task 5.

Known implementation caution:

- The service starts background work with `Task.Run`. This is acceptable for this project slice because existing code already uses fire-and-forget background work in controllers/services. If this grows, replace with a hosted queue service.
