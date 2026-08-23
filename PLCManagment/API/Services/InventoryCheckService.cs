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
        private static readonly Regex TrayRegex = new(
            @"^\s*(?<prefix>[^-\s]+)-(?<number>\d+)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly ConcurrentDictionary<string, long> RunningPlcs = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<long, byte> SchedulingInboundItems = new();
        private const int LoadingPointPollingMilliseconds = 1000;
        private const int OutboundCompletionMaxAttempts = 750;
        private const int OutboundCompletionPollingMilliseconds = 400;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<InventoryCheckService> _logger;

        public InventoryCheckService(
            IServiceScopeFactory serviceScopeFactory,
            ILogger<InventoryCheckService> logger)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _logger = logger;
        }

        public async Task<InventoryCheckTaskResponse> CreateOutboundRangeTaskAsync(InventoryCheckOutboundRangeRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var plcId = NormalizePlcId(request.PlcId);
            var range = ParseTrayRange(request.TrayStart, request.TrayEnd);
            var trayPrefix = $"{range.Prefix}-";

            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            try
            {
                await CancelActiveTasksForNewTask(context, plcId);
                RunningPlcs.TryRemove(plcId, out _);

                var candidates = await context.LocationManagements
                    .AsNoTracking()
                    .Where(location =>
                        location.PLCID == plcId &&
                        location.ShelfStatus == 1 &&
                        location.Tray != null &&
                        location.Tray.ToUpper().StartsWith(trayPrefix))
                    .ToListAsync();

                var matchedLocations = candidates
                    .Select(location => new { Location = location, Tray = TryParseTray(location.Tray!) })
                    .Where(item =>
                        item.Tray is not null &&
                        string.Equals(item.Tray.Value.Prefix, range.Prefix, StringComparison.OrdinalIgnoreCase) &&
                        item.Tray.Value.Number >= range.Start &&
                        item.Tray.Value.Number <= range.End)
                    .OrderBy(item => item.Tray!.Value.Number)
                    .ThenBy(item => item.Location.Shelf)
                    .ThenBy(item => item.Location.Position)
                    .ToList();

                if (matchedLocations.Count == 0)
                {
                    throw new KeyNotFoundException($"No occupied locations were found for PLC {plcId} in tray range {request.TrayStart} - {request.TrayEnd}.");
                }

                var task = new InventoryCheckTask
                {
                    TaskNo = CreateTaskNo(),
                    PLCID = plcId,
                    TrayStart = request.TrayStart.Trim(),
                    TrayEnd = request.TrayEnd.Trim(),
                    Status = InventoryCheckStatuses.Pending,
                    TotalCount = matchedLocations.Count,
                    CreatedAt = DateTime.Now,
                    Message = "盘点下架任务已创建。"
                };

                foreach (var matched in matchedLocations)
                {
                    task.Items.Add(new InventoryCheckItem
                    {
                        PLCID = matched.Location.PLCID,
                        Shelf = matched.Location.Shelf,
                        Position = matched.Location.Position,
                        Tray = matched.Location.Tray!,
                        OriginalShelfStatus = matched.Location.ShelfStatus,
                        OutboundStatus = InventoryCheckStatuses.Pending,
                        InboundStatus = InventoryCheckStatuses.NotReady
                    });
                }

                context.InventoryCheckTasks.Add(task);
                await context.SaveChangesAsync();

                if (!RunningPlcs.TryAdd(plcId, task.Id))
                {
                    await CancelActiveTasksForNewTask(context, plcId);
                    RunningPlcs[plcId] = task.Id;
                }

                _ = Task.Run(() => ProcessOutboundTaskAsync(task.Id));

                return MapTask(task);
            }
            catch
            {
                RunningPlcs.TryRemove(new KeyValuePair<string, long>(plcId, 0));
                throw;
            }
        }

        public async Task<InventoryCheckTaskResponse?> GetTaskAsync(long taskId)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var task = await context.InventoryCheckTasks
                .Include(t => t.Items)
                .FirstOrDefaultAsync(t => t.Id == taskId);

            return task is null ? null : MapTask(task);
        }

        public async Task<InventoryCheckTaskResponse> ScheduleInboundItemAsync(long itemId)
        {
            if (!SchedulingInboundItems.TryAdd(itemId, 0))
            {
                throw new InvalidOperationException("该货框正在预约上架，请勿重复操作。");
            }

            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var plcService = scope.ServiceProvider.GetRequiredService<IPlcService>();

            try
            {
                var item = await context.InventoryCheckItems
                    .Include(i => i.Task)
                    .FirstOrDefaultAsync(i => i.Id == itemId);

                if (item is null)
                {
                    throw new KeyNotFoundException("任务明细不存在。");
                }

                var task = await context.InventoryCheckTasks
                    .Include(t => t.Items)
                    .FirstOrDefaultAsync(t => t.Id == item.TaskId);

                if (task is null)
                {
                    throw new KeyNotFoundException("任务不存在。");
                }

                item = task.Items.First(i => i.Id == itemId);
                if (item.OutboundStatus != InventoryCheckStatuses.Succeeded ||
                    item.InboundStatus == InventoryCheckStatuses.Scheduled ||
                    item.InboundStatus == InventoryCheckStatuses.Running)
                {
                    throw new InvalidOperationException("该货框当前不能预约上架。");
                }

                item.InboundScheduledAt = DateTime.Now;

                if (!item.OutboundLoadingPoint.HasValue)
                {
                    item.InboundStatus = InventoryCheckStatuses.Failed;
                    item.InboundMessage = "缺少下架装载点，无法预约上架。";
                    await context.SaveChangesAsync();
                    return MapTask(task);
                }

                item.InboundLoadingPoint = item.OutboundLoadingPoint.Value;

                try
                {
                    item.InboundStatus = InventoryCheckStatuses.Running;
                    await context.SaveChangesAsync();

                    var result = await plcService.InboundOperation(
                        item.PLCID,
                        item.Shelf,
                        item.Position,
                        item.OutboundLoadingPoint.Value);

                    item.InboundStatus = result.IsSuccess
                        ? InventoryCheckStatuses.Scheduled
                        : InventoryCheckStatuses.Failed;
                    item.InboundMessage = string.IsNullOrWhiteSpace(result.Message)
                        ? (result.IsSuccess ? "已预约上架。" : "预约上架失败。")
                        : result.Message;
                }
                catch (Exception ex)
                {
                    item.InboundStatus = InventoryCheckStatuses.Failed;
                    item.InboundMessage = $"预约上架失败：{ex.Message}";
                    _logger.LogError(ex, "Inventory check task {TaskId} failed to schedule inbound for item {ItemId}", task.Id, item.Id);
                }

                await context.SaveChangesAsync();
                return MapTask(task);
            }
            finally
            {
                SchedulingInboundItems.TryRemove(itemId, out _);
            }
        }

        public async Task<InventoryCheckTaskResponse> CancelTaskAsync(long taskId)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var task = await context.InventoryCheckTasks
                .Include(t => t.Items)
                .FirstOrDefaultAsync(t => t.Id == taskId);

            if (task is null)
            {
                throw new KeyNotFoundException("任务不存在。");
            }

            CancelTask(task, "任务已由用户放弃。");
            await context.SaveChangesAsync();
            RunningPlcs.TryRemove(new KeyValuePair<string, long>(task.PLCID, task.Id));

            return MapTask(task);
        }

        private async Task ProcessOutboundTaskAsync(long taskId)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var plcService = scope.ServiceProvider.GetRequiredService<IPlcService>();
            var locationCheckService = scope.ServiceProvider.GetRequiredService<ILocationCheckService>();

            InventoryCheckTask? task = null;
            var plcId = string.Empty;
            var lockTaken = false;

            try
            {
                task = await context.InventoryCheckTasks
                    .Include(t => t.Items)
                    .FirstOrDefaultAsync(t => t.Id == taskId);

                if (task is null)
                {
                    _logger.LogWarning("Inventory check task {TaskId} does not exist; outbound cannot run.", taskId);
                    return;
                }

                plcId = task.PLCID;
                lockTaken = RunningPlcs.TryAdd(plcId, task.Id);
                if (!lockTaken)
                {
                    if (!RunningPlcs.TryGetValue(plcId, out var runningTaskId) || runningTaskId != task.Id)
                    {
                        task.Status = InventoryCheckStatuses.Canceled;
                        task.CompletedAt = DateTime.Now;
                        task.Message = $"PLC {plcId} 已创建新任务，当前任务已被替换。";
                        await context.SaveChangesAsync();
                        return;
                    }

                    lockTaken = true;
                }

                task.Status = InventoryCheckStatuses.Running;
                task.StartedAt = DateTime.Now;
                task.Message = "盘点下架任务正在执行。";
                await context.SaveChangesAsync();

                foreach (var item in task.Items.OrderBy(i => i.Id))
                {
                    if (await IsTaskCanceled(context, task, plcId))
                    {
                        break;
                    }

                    item.OutboundStatus = InventoryCheckStatuses.Running;
                    item.OutboundStartedAt = DateTime.Now;
                    item.OutboundMessage = "正在执行下架。";
                    await context.SaveChangesAsync();

                    try
                    {
                        var loadingPoint = await WaitForEmptyLoadingPoint(context, task, item, locationCheckService);
                        if (loadingPoint.IsCanceled)
                        {
                            item.OutboundStatus = InventoryCheckStatuses.Canceled;
                            item.InboundStatus = InventoryCheckStatuses.NotReady;
                            item.OutboundCompletedAt = DateTime.Now;
                            item.OutboundMessage = task.Message;
                            await context.SaveChangesAsync();
                            break;
                        }

                        item.OutboundLoadingPoint = loadingPoint.LoadingPoint!.Value;

                        var result = await WaitForOutboundCommandWhenPlcAvailable(
                            context,
                            task,
                            item,
                            plcService,
                            loadingPoint.LoadingPoint.Value);

                        if (result.IsCanceled)
                        {
                            item.OutboundStatus = InventoryCheckStatuses.Canceled;
                            item.InboundStatus = InventoryCheckStatuses.NotReady;
                            item.OutboundCompletedAt = DateTime.Now;
                            item.OutboundMessage = task.Message;
                            await context.SaveChangesAsync();
                            break;
                        }

                        if (!result.IsSuccess)
                        {
                            item.OutboundCompletedAt = DateTime.Now;
                            item.OutboundStatus = InventoryCheckStatuses.Failed;
                            item.InboundStatus = InventoryCheckStatuses.NotReady;
                            item.OutboundMessage = string.IsNullOrWhiteSpace(result.Message)
                                ? "下架指令执行失败。"
                                : result.Message;
                            await context.SaveChangesAsync();
                            continue;
                        }

                        var completion = await WaitForOutboundCompletionAndSyncPallet(
                            plcService,
                            locationCheckService,
                            item.PLCID,
                            loadingPoint.LoadingPoint.Value,
                            item.Tray);

                        item.OutboundCompletedAt = DateTime.Now;
                        item.OutboundStatus = completion.IsSuccess
                            ? InventoryCheckStatuses.Succeeded
                            : InventoryCheckStatuses.Failed;
                        item.InboundStatus = completion.IsSuccess
                            ? InventoryCheckStatuses.Ready
                            : InventoryCheckStatuses.NotReady;
                        item.OutboundMessage = completion.Message;
                    }
                    catch (Exception ex)
                    {
                        item.OutboundStatus = InventoryCheckStatuses.Failed;
                        item.InboundStatus = InventoryCheckStatuses.NotReady;
                        item.OutboundCompletedAt = DateTime.Now;
                        item.OutboundMessage = $"下架失败：{ex.Message}";
                        _logger.LogError(ex, "Inventory check task {TaskId} failed outbound for item {ItemId}", taskId, item.Id);
                    }

                    await context.SaveChangesAsync();
                }

                task.SuccessCount = task.Items.Count(i => i.OutboundStatus == InventoryCheckStatuses.Succeeded);
                task.FailedCount = task.Items.Count(i => i.OutboundStatus == InventoryCheckStatuses.Failed);
                task.CompletedAt ??= DateTime.Now;
                if (task.Status != InventoryCheckStatuses.Canceled)
                {
                    task.Status = task.FailedCount == 0
                        ? InventoryCheckStatuses.Completed
                        : InventoryCheckStatuses.CompletedWithErrors;
                    task.Message = $"盘点下架完成。成功：{task.SuccessCount}，失败：{task.FailedCount}。";
                }
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inventory check task {TaskId} failed.", taskId);

                if (task is not null)
                {
                    task.Status = InventoryCheckStatuses.Failed;
                    task.CompletedAt = DateTime.Now;
                    task.Message = $"盘点下架任务失败：{ex.Message}";
                    await context.SaveChangesAsync();
                }
            }
            finally
            {
                if (lockTaken && !string.IsNullOrWhiteSpace(plcId))
                {
                    RunningPlcs.TryRemove(new KeyValuePair<string, long>(plcId, taskId));
                }
            }
        }

        private static async Task CancelActiveTasksForNewTask(ApplicationDbContext context, string plcId)
        {
            var activeTasks = await context.InventoryCheckTasks
                .Include(t => t.Items)
                .Where(t =>
                    t.PLCID == plcId &&
                    (t.Status == InventoryCheckStatuses.Pending || t.Status == InventoryCheckStatuses.Running))
                .ToListAsync();

            foreach (var activeTask in activeTasks)
            {
                CancelTask(activeTask, "同一立库已创建新任务，原任务已放弃。");
            }

            if (activeTasks.Count > 0)
            {
                await context.SaveChangesAsync();
            }
        }

        private static void CancelTask(InventoryCheckTask task, string message)
        {
            task.Status = InventoryCheckStatuses.Canceled;
            task.CompletedAt = DateTime.Now;
            task.Message = message;

            foreach (var item in task.Items.Where(i =>
                i.OutboundStatus == InventoryCheckStatuses.Pending ||
                i.OutboundStatus == InventoryCheckStatuses.Running))
            {
                item.OutboundStatus = InventoryCheckStatuses.Canceled;
                item.InboundStatus = InventoryCheckStatuses.NotReady;
                item.OutboundCompletedAt = DateTime.Now;
                item.OutboundMessage = message;
            }
        }

        private async Task<(bool IsSuccess, bool IsCanceled, string Message)> WaitForOutboundCommandWhenPlcAvailable(
            ApplicationDbContext context,
            InventoryCheckTask task,
            InventoryCheckItem item,
            IPlcService plcService,
            int loadingPoint,
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (await IsTaskCanceled(context, task, item.PLCID))
                {
                    return (false, true, task.Message);
                }

                var result = await plcService.OutboundOperation(
                    item.PLCID,
                    item.Shelf,
                    item.Position,
                    loadingPoint);

                if (result.IsSuccess || !IsTransientPlcUnavailable(result.Message))
                {
                    return (result.IsSuccess, false, result.Message ?? string.Empty);
                }

                item.OutboundMessage = $"等待 PLC 恢复可用：{result.Message}";
                await context.SaveChangesAsync(cancellationToken);
                await Task.Delay(LoadingPointPollingMilliseconds, cancellationToken);
            }
        }

        private bool IsTransientPlcUnavailable(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.Contains(" is offline. Retry after ", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Modbus probe failed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Connection failed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("PLC连接失败", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("连接PLC", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("连接尝试失败", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("设备当前任务未完成", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("无法连接", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<(bool IsSuccess, string Message)> WaitForOutboundCompletionAndSyncPallet(
            IPlcService plcService,
            ILocationCheckService locationCheckService,
            string plcId,
            int loadingPoint,
            string tray)
        {
            for (var attempt = 1; attempt <= OutboundCompletionMaxAttempts; attempt++)
            {
                await Task.Delay(OutboundCompletionPollingMilliseconds);

                var plcStatus = await plcService.UpdatePlcStatusAsync(plcId);
                if (plcStatus.OperationResult != 6 && plcStatus.OperationResult != 0)
                {
                    continue;
                }

                var updated = await locationCheckService.UpdateLoadingPointPallet(plcId, loadingPoint, tray);
                if (!updated)
                {
                    return (false, $"下架已完成，但装载点 {loadingPoint} 未接收货框 {tray}。");
                }

                return (true, "下架完成，货框已同步到装载点。");
            }

            return (false, "等待 PLC 下架完成超时。");
        }

        private static async Task<(int? LoadingPoint, bool IsCanceled)> WaitForEmptyLoadingPoint(
            ApplicationDbContext context,
            InventoryCheckTask task,
            InventoryCheckItem item,
            ILocationCheckService locationCheckService,
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (await IsTaskCanceled(context, task, item.PLCID))
                {
                    return (null, true);
                }

                item.OutboundMessage = "等待装载点 0 或 1 空闲。";
                await context.SaveChangesAsync(cancellationToken);

                var plcId = item.PLCID;
                if (await locationCheckService.IsLoadingPointEmpty(plcId, 0))
                {
                    return (0, false);
                }

                if (await locationCheckService.IsLoadingPointEmpty(plcId, 1))
                {
                    return (1, false);
                }

                await Task.Delay(LoadingPointPollingMilliseconds, cancellationToken);
            }
        }

        private static async Task<bool> IsTaskCanceled(ApplicationDbContext context, InventoryCheckTask task, string plcId)
        {
            await context.Entry(task).ReloadAsync();
            if (task.Status == InventoryCheckStatuses.Canceled)
            {
                return true;
            }

            if (RunningPlcs.TryGetValue(plcId, out var runningTaskId) && runningTaskId != task.Id)
            {
                CancelTask(task, "同一立库已创建新任务，原任务已放弃。");
                await context.SaveChangesAsync();
                return true;
            }

            return false;
        }

        private static string NormalizePlcId(string plcId)
        {
            var normalized = (plcId ?? string.Empty).Trim().ToUpperInvariant();
            if (!Regex.IsMatch(normalized, @"^A[1-8]$"))
            {
                throw new ArgumentException("PLC id must be A1 through A8.");
            }

            return normalized;
        }

        private static (string Prefix, int Start, int End) ParseTrayRange(string trayStart, string trayEnd)
        {
            var start = ParseTray(trayStart);
            var end = ParseTray(trayEnd);

            if (!string.Equals(start.Prefix, end.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Tray range prefixes must match.");
            }

            if (start.Number > end.Number)
            {
                throw new ArgumentException("Tray range start cannot be greater than the end.");
            }

            return (start.Prefix.ToUpperInvariant(), start.Number, end.Number);
        }

        private static (string Prefix, int Number) ParseTray(string tray)
        {
            var parsed = TryParseTray(tray);
            if (parsed is null)
            {
                throw new ArgumentException("Tray code format must be prefix-number, for example A10-701.");
            }

            return parsed.Value;
        }

        private static (string Prefix, int Number)? TryParseTray(string tray)
        {
            if (string.IsNullOrWhiteSpace(tray))
            {
                return null;
            }

            var match = TrayRegex.Match(tray);
            if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var number))
            {
                return null;
            }

            return (match.Groups["prefix"].Value.ToUpperInvariant(), number);
        }

        private static string CreateTaskNo()
        {
            return $"IC{DateTime.Now:yyyyMMddHHmmssfff}{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
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
                Items = task.Items
                    .OrderBy(item => item.Id)
                    .Select(item => new InventoryCheckItemResponse
                    {
                        Id = item.Id,
                        PLCID = item.PLCID,
                        Tray = item.Tray,
                        Shelf = item.Shelf,
                        Position = item.Position,
                        OutboundLoadingPoint = item.OutboundLoadingPoint,
                        OutboundStatus = item.OutboundStatus,
                        OutboundMessage = item.OutboundMessage,
                        InboundStatus = item.InboundStatus,
                        InboundLoadingPoint = item.InboundLoadingPoint,
                        InboundMessage = item.InboundMessage
                    })
                    .ToList()
            };
        }
    }
}
