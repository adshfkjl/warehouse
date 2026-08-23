using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Dapper;
using PlcManagementService.Models;
using PLCService.Configuration;
using PLCService.Models;
using PLCService.Services;
using Timer = System.Timers.Timer;

namespace PLCService
{
    public partial class PLCService : ServiceBase
    {
        // 移除全局定时器
        // private Timer _timer;
        private Timer _autoRunUpdateTimer;
        private DatabaseService _dbService;
        private ConcurrentDictionary<string, ModbusService> _modbusServices;

        // 存储每个PLC的独立定时器
        private ConcurrentDictionary<string, PlcTimerController> _plcTimerControllers = new ConcurrentDictionary<string, PlcTimerController>();

        // 存储PLC的AutoRun状态缓存
        private ConcurrentDictionary<string, bool> _plcAutoRunCache = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, byte> _processingPlcs = new ConcurrentDictionary<string, byte>();
        private int _isAutoRunUpdating;

        // 默认刷新间隔配置
        private int _normalReadInterval = AppSettings.ReadInterval;     // 正常间隔：1500ms
        private int _fastReadInterval = AppSettings.FastReadInterval;   // 快速间隔：200ms

        // 工作状态常量定义
        private const int OPERATION_RESULT_IDLE = 0;     // 空闲/完工状态
        private const int OPERATION_RESULT_PAUSED = 11;  // 暂停状态

        private const int AUTO_RUN_UPDATE_INTERVAL = 3000; // 5秒更新一次AutoRun

        public PLCService()
        {
            ServiceName = "PLCService";
            CanStop = true;
            CanPauseAndContinue = false;
            AutoLog = true;

            _dbService = new DatabaseService();
            _modbusServices = new ConcurrentDictionary<string, ModbusService>();
        }

        protected override void OnStart(string[] args)
        {
            LogService.Info("PLC服务启动中...");

            try
            {
                // 1. 移除全局定时器，为每个PLC创建独立的定时器
                // 这个在ProcessPlcWithDynamicInterval中按需创建

                // 2. AutoRun状态更新定时器
                _autoRunUpdateTimer = new Timer(AUTO_RUN_UPDATE_INTERVAL);
                _autoRunUpdateTimer.Elapsed += async (s, e) =>
                {
                    if (Interlocked.Exchange(ref _isAutoRunUpdating, 1) == 1)
                    {
                        LogService.Debug("上一次PLC AutoRun状态更新尚未完成，跳过本次触发");
                        return;
                    }

                    try
                    {
                        await UpdatePlcAutoRunFromDatabase();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isAutoRunUpdating, 0);
                    }
                };
                _autoRunUpdateTimer.AutoReset = true;
                _autoRunUpdateTimer.Start();

                LogService.Info($"PLC服务启动完成，正常读取间隔: {_normalReadInterval}ms，快速读取间隔: {_fastReadInterval}ms，AutoRun更新间隔: {AUTO_RUN_UPDATE_INTERVAL}ms");

                // 3. 初始处理一次，为每个活跃PLC创建定时器
                Task.Run(async () =>
                {
                    await InitializePlcTimers();
                });
            }
            catch (Exception ex)
            {
                LogService.Error($"服务启动失败: {ex.Message}");
                throw;
            }
        }

        protected override void OnStop()
        {
            LogService.Info("PLC服务停止中...");

            try
            {
                // 停止并释放所有PLC定时器
                foreach (var controller in _plcTimerControllers.Values)
                {
                    controller.Stop();
                    controller.Dispose();
                }
                _plcTimerControllers.Clear();

                _autoRunUpdateTimer?.Stop();
                _autoRunUpdateTimer?.Dispose();
                _autoRunUpdateTimer = null;

                foreach (var service in _modbusServices.Values)
                {
                    service.ConnectionStatusChanged -= OnConnectionStatusChanged;
                    service.Dispose();
                }

                _modbusServices.Clear();
                _plcAutoRunCache.Clear();

                LogService.Info("PLC服务已停止");
            }
            catch (Exception ex)
            {
                LogService.Error($"服务停止过程中发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 初始化PLC定时器
        /// </summary>
        private async Task InitializePlcTimers()
        {
            try
            {
                var activePlcs = _dbService.GetActivePlcs();
                foreach (var plc in activePlcs)
                {
                    await InitializePlcTimer(plc);
                }
                LogService.Info($"已为 {activePlcs.Count()} 个活跃PLC初始化定时器");
            }
            catch (Exception ex)
            {
                LogService.Error($"初始化PLC定时器失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 初始化单个PLC的定时器
        /// </summary>
        private async Task InitializePlcTimer(PlcConfiguration plc)
        {
            try
            {
                if (!_plcTimerControllers.ContainsKey(plc.PlcId))
                {
                    var controller = new PlcTimerController(plc.PlcId, _normalReadInterval);
                    controller.TimerElapsed += async (plcId) =>
                    {
                        await ProcessSinglePlc(plcId);
                    };

                    _plcTimerControllers[plc.PlcId] = controller;

                    // 初始读取一次PLC数据以确定正确的刷新频率
                    await ProcessSinglePlc(plc.PlcId);
                    controller.Start();

                    LogService.Info($"PLC {plc.PlcId} 定时器已初始化，初始间隔: {_normalReadInterval}ms");
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"初始化PLC {plc.PlcId} 定时器失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理单个PLC（独立定时器触发）
        /// </summary>
        private async Task ProcessSinglePlc(string plcId)
        {
            if (!_processingPlcs.TryAdd(plcId, 0))
            {
                LogService.Debug($"PLC {plcId} 上一次处理尚未完成，跳过本次触发");
                return;
            }

            try
            {
                // 从数据库获取最新的PLC配置
                var plc = _dbService.GetPlcConfiguration(plcId);
                if (plc == null)
                {
                    LogService.Warning($"PLC {plcId} 配置不存在，停止定时器");
                    StopPlcTimer(plcId);
                    return;
                }

                await ProcessPlcWithIndependentTimer(plc);
            }
            catch (Exception ex)
            {
                LogService.Error($"处理PLC {plcId} 时发生错误: {ex.Message}");
            }
            finally
            {
                _processingPlcs.TryRemove(plcId, out _);
            }
        }

        /// <summary>
        /// 处理单个PLC，使用独立定时器
        /// </summary>
        private async Task ProcessPlcWithIndependentTimer(PlcConfiguration plc)
        {
            try
            {
                if (!_modbusServices.TryGetValue(plc.PlcId, out var modbusService))
                {
                    modbusService = new ModbusService(plc);
                    modbusService.ConnectionStatusChanged += OnConnectionStatusChanged;
                    if (!_modbusServices.TryAdd(plc.PlcId, modbusService))
                    {
                        modbusService.ConnectionStatusChanged -= OnConnectionStatusChanged;
                        modbusService.Dispose();
                        modbusService = _modbusServices[plc.PlcId];
                    }

                    // 从数据库获取最新的AutoRun值
                    var dbPlcConfig = _dbService.GetPlcConfiguration(plc.PlcId);
                    if (dbPlcConfig != null)
                    {
                        plc.AutoRun = dbPlcConfig.AutoRun;
                        _plcAutoRunCache[plc.PlcId] = dbPlcConfig.AutoRun;
                        LogService.Info($"PLC {plc.PlcId} 初始化AutoRun状态: {plc.AutoRun}");
                    }

                    await modbusService.ConnectAsync();
                }

                // 读取PLC数据
                var updatedPlc = await modbusService.ReadPlcDataAsync();

                // 确保使用最新的AutoRun值
                if (_plcAutoRunCache.TryGetValue(plc.PlcId, out bool currentAutoRun))
                {
                    updatedPlc.AutoRun = currentAutoRun;
                }

                // 检查是否需要快速刷新
                bool requiresFastRefresh = CheckIfRequiresFastRefresh(updatedPlc, modbusService);

                // 动态调整该PLC的定时器间隔
                AdjustPlcTimerInterval(plc.PlcId, requiresFastRefresh);

                if (modbusService.IsConnected)
                {
                    long? operationid = null;
                    LogService.Info($"PLC {updatedPlc.PlcId} 自动任务检查: AutoRun={updatedPlc.AutoRun}, OperationResult={updatedPlc.OperationResult}, BoxWeightA={updatedPlc.BoxWeightA}, BoxWeightB={updatedPlc.BoxWeightB}, ForksStat={updatedPlc.ForksStat}, Connected={modbusService.IsConnected}");

                    _dbService.UpdatePlcStatus(updatedPlc);

                    // PLC空闲且开启自动模式时才查询自动任务。具体上架/下架条件在取到任务后分别判断。
                    if (ShouldCheckAutoTask(updatedPlc))
                    {
                        operationid = await ProcessAutoOutbound(updatedPlc, modbusService);
                    }
                    else
                    {
                        LogService.Info($"PLC {updatedPlc.PlcId} 跳过自动任务查询: {GetAutoTaskSkipReason(updatedPlc)}");
                    }

                    // 重新读取数据以获取最新状态
                    updatedPlc = await modbusService.ReadPlcDataAsync();
                    updatedPlc.OperationID = operationid;

                    // 连接正常时更新所有数据
                    _dbService.UpdatePlcStatus(updatedPlc);
                }
                else
                {
                    // 连接异常时只更新连接状态
                    _dbService.UpdateConnectionStatusOnly(updatedPlc);
                    // 连接异常时使用正常刷新间隔
                    AdjustPlcTimerInterval(plc.PlcId, false);
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"处理PLC {plc.PlcId} 时发生错误: {ex.Message}");

                // 更新错误状态到数据库
                plc.LastConnectionStatus = "Error";
                plc.LastErrorMessage = ex.Message;
                plc.LastTestTime = DateTime.Now;
                _dbService.UpdateConnectionStatusOnly(plc);
                // 发生错误时使用正常刷新间隔
                AdjustPlcTimerInterval(plc.PlcId, false);
            }
        }

        /// <summary>
        /// 调整单个PLC的定时器间隔
        /// </summary>
        private void AdjustPlcTimerInterval(string plcId, bool requiresFastRefresh)
        {
            try
            {
                if (_plcTimerControllers.TryGetValue(plcId, out var controller))
                {
                    int newInterval = requiresFastRefresh ? _fastReadInterval : _normalReadInterval;

                    if (controller.CurrentInterval != newInterval)
                    {
                        controller.AdjustInterval(newInterval);

                        string mode = requiresFastRefresh ? "快速" : "正常";
                        LogService.Debug($"PLC {plcId} 定时器间隔调整为: {newInterval}ms ({mode}模式)");
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"调整PLC {plcId} 定时器间隔失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止指定PLC的定时器
        /// </summary>
        private void StopPlcTimer(string plcId)
        {
            try
            {
                if (_plcTimerControllers.TryGetValue(plcId, out var controller))
                {
                    controller.Stop();
                    controller.Dispose();
                    _plcTimerControllers.TryRemove(plcId, out _);
                    LogService.Info($"PLC {plcId} 定时器已停止");
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"停止PLC {plcId} 定时器失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查是否需要快速刷新
        /// </summary>
        private bool CheckIfRequiresFastRefresh(PlcConfiguration plc, ModbusService modbusService)
        {
            if (!modbusService.IsConnected)
            {
                return false; // 连接不正常，不需要快速刷新
            }

            // 检查OperationResult值
            bool isIdleOrPaused = (plc.OperationResult == OPERATION_RESULT_IDLE ||
                                   plc.OperationResult == OPERATION_RESULT_PAUSED);

            // 如果不是空闲或暂停状态，需要快速刷新
            return !isIdleOrPaused;
        }

        private bool ShouldCheckAutoTask(PlcConfiguration plc)
        {
            return plc.AutoRun && plc.OperationResult == 0;
        }

        private string GetAutoTaskSkipReason(PlcConfiguration plc)
        {
            if (!plc.AutoRun)
            {
                return "AutoRun为false";
            }

            if (plc.OperationResult != 0)
            {
                return $"PLC当前任务未完成，OperationResult={plc.OperationResult}";
            }

            return $"未知原因，AutoRun={plc.AutoRun}, OperationResult={plc.OperationResult}, BoxWeightA={plc.BoxWeightA}, BoxWeightB={plc.BoxWeightB}";
        }

        // 处理自动下架任务
        private async Task<long?> ProcessAutoOutbound(PlcConfiguration plc, ModbusService modbusService)
        {
            long? operationId = -1;

            LogService.Info($"处理自动任务: {plc.PlcId} ({plc.IpAddress}:{plc.Port})");
            try
            {
                // 获取需要下架的业务信息
                var autoRunBills = _dbService.GetAutoRunBill(plc.PlcId);
                var billList = autoRunBills?.ToList() ?? new List<AutoRunBill>();
                LogService.Info($"PLC {plc.PlcId} 自动任务查询完成，任务数量: {billList.Count}");
                var bill = billList.FirstOrDefault();

                if (bill == null)
                {
                    LogService.Info($"PLC {plc.PlcId} 没有待执行自动任务");
                    return operationId;
                }

                LogService.Info($"PLC {plc.PlcId} 获取到自动任务: OperationType={bill.OperationType}({GetOperationTypeName(bill.OperationType)}), BillID={bill.BillID}, BillNo={bill.BillNo}, ITM={bill.ITM}, Tray={bill.Tray}, Shelf={bill.Shelf}, Position={bill.Position}, PLCLocationCode={bill.PLCLocationCode}");

                //执行新任务前，保存当前PLC信息到日志中
                plc = await modbusService.ReadPlcDataAsync();
                LogPlcSnapshot("执行自动任务前PLC状态", plc);
                _dbService.UpdatePlcStatus(plc);

                if (bill.OperationType == 1)
                {

                    LogService.Info($"PLC {plc.PlcId} 开始处理自动下架任务: {bill.BillID}-{bill.BillNo}-{bill.ITM}");

                    if (!TryDetermineOutboundLoadingPoint(plc, out int loadingPoint, out string loadingPointReason))
                    {
                        operationId = -1;
                        LogService.Warning($"PLC {plc.PlcId} 自动下架跳过: {loadingPointReason}, BillID={bill.BillID}, BillNo={bill.BillNo}, ITM={bill.ITM}, BoxWeightA={plc.BoxWeightA}, BoxWeightB={plc.BoxWeightB}");
                        return operationId;
                    }

                    LogService.Info($"PLC {plc.PlcId} 自动下架装载点已确定: LoadingPoint={loadingPoint}, 原始PLCLocationCode={bill.PLCLocationCode}, {loadingPointReason}");

                    // 执行下架操作
                    operationId = await ExecuteAutoOutbound(plc, modbusService, bill, loadingPoint);
                }
                else if (bill.OperationType == 0)
                {
                    LogService.Info($"PLC {plc.PlcId} 开始处理自动上架任务: {bill.BillID}-{bill.BillNo}-{bill.ITM}");
                    LogService.Info($"PLC {plc.PlcId} 自动上架参数: Shelf={bill.Shelf}, Position={bill.Position}, LoadingPoint={bill.PLCLocationCode}, Tray={bill.Tray}, BoxWeightA={plc.BoxWeightA}, BoxWeightB={plc.BoxWeightB}, OperationResult={plc.OperationResult}, ForksStat={plc.ForksStat}");

                    // 执行上架操作
                    operationId = await ExecuteAutoInbound(plc, modbusService, bill, bill.PLCLocationCode);
                }
                else
                {
                    LogService.Warning($"PLC {plc.PlcId} 获取到未知自动任务类型: OperationType={bill.OperationType}, BillID={bill.BillID}, BillNo={bill.BillNo}, ITM={bill.ITM}");
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"处理PLC {plc.PlcId} 自动任务时发生错误: {ex.Message}");
            }
            return operationId;
        }

        // 确定自动下架装载点
        private bool TryDetermineOutboundLoadingPoint(PlcConfiguration plc, out int loadingPoint, out string reason)
        {
            loadingPoint = -1;

            var loadingPointStatuses = _dbService.GetLoadingPointStatuses(plc.PlcId).ToList();
            var point0 = loadingPointStatuses.FirstOrDefault(x => x.PLCLocationCode == 0);
            var point1 = loadingPointStatuses.FirstOrDefault(x => x.PLCLocationCode == 1);

            bool point0HasPallet = !string.IsNullOrWhiteSpace(point0?.CurrentPalletNumber);
            bool point1HasPallet = !string.IsNullOrWhiteSpace(point1?.CurrentPalletNumber);

            if (plc.BoxWeightA > 2m && plc.BoxWeightB > 2m)
            {
                reason = "BoxWeightA和BoxWeightB都大于2，两个装载点都有货";
                return false;
            }

            if (point0HasPallet && point1HasPallet)
            {
                reason = "LoadingPoint_Status两个装载点CurrentPalletNumber都不为空，两个装载点都有货";
                return false;
            }

            if (plc.BoxWeightA < 2m && !point0HasPallet)
            {
                loadingPoint = 0;
                reason = "BoxWeightA小于2且0号装载点CurrentPalletNumber为空";
                return true;
            }

            if (plc.BoxWeightB < 2m && !point1HasPallet)
            {
                loadingPoint = 1;
                reason = "BoxWeightB小于2且1号装载点CurrentPalletNumber为空";
                return true;
            }

            reason = $"没有可用装载点: 0号重量={plc.BoxWeightA}, 0号CurrentPalletNumber={point0?.CurrentPalletNumber ?? "空"}; 1号重量={plc.BoxWeightB}, 1号CurrentPalletNumber={point1?.CurrentPalletNumber ?? "空"}";
            return false;
        }

        private static string GetOperationTypeName(int operationType)
        {
            return operationType == 0 ? "上架" :
                   operationType == 1 ? "下架" :
                   "未知";
        }

        private static void LogPlcSnapshot(string title, PlcConfiguration plc)
        {
            if (plc == null)
            {
                LogService.Warning($"{title}: PLC对象为空");
                return;
            }

            LogService.Info($"{title}: PLC={plc.PlcId}, ConnectedStatus={plc.LastConnectionStatus}, Error={plc.LastErrorMessage}, OperationResult={plc.OperationResult}, AutoRun={plc.AutoRun}, BoxWeightA={plc.BoxWeightA}, BoxWeightB={plc.BoxWeightB}, ForksStat={plc.ForksStat}, InboundCompleted={plc.IsInboundCompleted}, OutboundCompleted={plc.IsOutboundCompleted}, PosX={plc.PosX}, PosY={plc.PosY}, PosZ={plc.PosZ}, OperationID={plc.OperationID}");
        }

        // 执行自动下架操作
        private async Task<long?> ExecuteAutoOutbound(PlcConfiguration plc, ModbusService modbusService,
                                              AutoRunBill bill, int loadingPoint)
        {
            long? operationId = -1;

            try
            {
                LogService.Info($"PLC {plc.PlcId} 准备执行自动下架: BillID={bill.BillID}, BillNo={bill.BillNo}, ITM={bill.ITM}, Tray={bill.Tray}, Shelf={bill.Shelf}, Position={bill.Position}, LoadingPoint={loadingPoint}");

                // 先记录操作日志并获取OperationID
                operationId = await LogOperationToDatabase(plc, bill, loadingPoint, 1, 4); // 初始状态为4（出库取货中）

                if (operationId == null)
                {
                    LogService.Error($"记录操作日志失败，跳过下架操作");
                    return operationId;
                }
                plc.OperationID = operationId;
                _dbService.UpdatePlcOperationId(plc.PlcId, operationId);

                // 向PLC写入下架信息
                LogService.Info($"PLC {plc.PlcId} 下架写寄存器开始: OperationID={operationId}");
                await modbusService.WriteRegisterAsync(22000, 1);     // 下架操作
                await modbusService.WriteRegisterAsync(22001, (ushort)loadingPoint);  // 装载点
                await modbusService.WriteRegisterAsync(22002, (ushort)bill.Shelf);    // 货架编号
                await modbusService.WriteRegisterAsync(22003, (ushort)bill.Position); // 储位编号
                await modbusService.WriteRegisterAsync(22009, 1);     // 启动PLC

                LogService.Info($"PLC {plc.PlcId} 下架指令已发送: 货架{bill.Shelf}, 储位{bill.Position}, 装载点{loadingPoint}, OperationID: {operationId}");

                decimal operationWeight = ResolveOperationWeight(plc, loadingPoint);
                StartAutoOutboundBillDetailCompletionMonitor(plc.PlcId, bill, loadingPoint, operationWeight);
                StartAutoOutboundPalletSyncMonitor(plc.PlcId, bill.Shelf, bill.Position, loadingPoint, bill.Tray);
            }
            catch (Exception ex)
            {
                LogService.Error($"向PLC {plc.PlcId} 发送下架指令失败: {ex.Message}, BillID={bill?.BillID}, BillNo={bill?.BillNo}, ITM={bill?.ITM}, OperationID={operationId}");
            }
            return operationId;
        }

        private void StartAutoOutboundPalletSyncMonitor(string plcId, int shelf, int position, int loadingPoint, string palletNumber)
        {
            Task.Run(async () =>
            {
                const int pollingIntervalMilliseconds = 400;
                const int maxAttempts = 750;
                string taskKey = $"{plcId}-{shelf}-{position}-{loadingPoint}";
                bool observedOutboundProgress = false;

                try
                {
                    LogService.Info($"[{taskKey}] 自动下架托盘同步监控启动，等待 PLC OperationResult 为 6 或 0");

                    if (string.IsNullOrWhiteSpace(palletNumber))
                    {
                        LogService.Warning($"[{taskKey}] 自动下架未获得托盘号，无法同步到装载点");
                        return;
                    }

                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        await Task.Delay(pollingIntervalMilliseconds);

                        var plcStatus = _dbService.GetPlcConfiguration(plcId);
                        if (plcStatus == null)
                        {
                            LogService.Warning($"[{taskKey}] 自动下架托盘同步监控未找到PLC配置");
                            return;
                        }

                        if (plcStatus.OperationResult != 0)
                        {
                            observedOutboundProgress = true;
                        }

                        // 部分PLC（A5尤为明显）的完成状态6保持时间很短，轮询可能只看到
                        // 运行中的非零状态随后直接回到空闲0。只有已观察到任务运行后，
                        // 才允许用0判定完成，避免把监控启动时的初始空闲状态误判为完成。
                        if (plcStatus.OperationResult != 6 && !(plcStatus.OperationResult == 0 && observedOutboundProgress))
                        {
                            continue;
                        }

                        int affectedRows = _dbService.UpdateLoadingPointPallet(plcId, loadingPoint, palletNumber);
                        if (affectedRows > 0)
                        {
                            LogService.Info($"[{taskKey}] 自动下架完成，同步托盘号到装载点成功: Tray={palletNumber}");
                        }
                        else
                        {
                            LogService.Warning($"[{taskKey}] 自动下架完成，但LoadingPoint_Status未更新任何记录: Tray={palletNumber}");
                        }

                        return;
                    }

                    LogService.Warning($"[{taskKey}] 自动下架托盘同步监控超时，未检测到 OperationResult 为 6 或 0");
                }
                catch (Exception ex)
                {
                    LogService.Error($"[{taskKey}] 自动下架托盘同步监控异常: {ex.Message}");
                }
            });
        }

        private void StartAutoOutboundBillDetailCompletionMonitor(string plcId, AutoRunBill bill, int loadingPoint, decimal operationWeight)
        {
            Task.Run(async () =>
            {
                const int pollingIntervalMilliseconds = 400;
                const int maxAttempts = 750;
                string taskKey = $"{plcId}-{bill.Shelf}-{bill.Position}-{loadingPoint}";
                bool observedOutboundProgress = false;

                try
                {
                    LogService.Info($"[{taskKey}] 自动下架单据完成监控启动，等待 PLC 出库完成后写入BillDetail.OutBoundTime");

                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        await Task.Delay(pollingIntervalMilliseconds);

                        var plcStatus = _dbService.GetPlcConfiguration(plcId);
                        if (plcStatus == null)
                        {
                            LogService.Warning($"[{taskKey}] 自动下架单据完成监控未找到PLC配置");
                            return;
                        }

                        if (plcStatus.OperationResult != 0)
                        {
                            observedOutboundProgress = true;
                        }

                        if (plcStatus.OperationResult != 6 && !(plcStatus.OperationResult == 0 && observedOutboundProgress))
                        {
                            continue;
                        }

                        using (var connection = new System.Data.SqlClient.SqlConnection(AppSettings.ConnectionString))
                        {
                            await connection.OpenAsync();
                            await ExecuteBillDetailOperationAsync(connection, plcStatus, bill, 1, 4, loadingPoint, operationWeight);
                        }

                        LogService.Info($"[{taskKey}] 自动下架完成，BillDetail.OutBoundTime写入成功: BillID={bill.BillID}, BillNo={bill.BillNo}, ITM={bill.ITM}, Tray={bill.Tray}");
                        return;
                    }

                    LogService.Warning($"[{taskKey}] 自动下架单据完成监控超时，未写入BillDetail.OutBoundTime: BillID={bill.BillID}, BillNo={bill.BillNo}, ITM={bill.ITM}");
                }
                catch (Exception ex)
                {
                    LogService.Error($"[{taskKey}] 自动下架单据完成监控异常: {ex.Message}");
                }
            });
        }


        // 执行自动上架操作
        private async Task<long?> ExecuteAutoInbound(PlcConfiguration plc, ModbusService modbusService,
                                              AutoRunBill bill, int loadingPoint)
        {
            long? operationId = -1;

            try
            {
                LogService.Info($"PLC {plc.PlcId} 准备执行自动上架: BillID={bill.BillID}, BillNo={bill.BillNo}, ITM={bill.ITM}, Tray={bill.Tray}, Shelf={bill.Shelf}, Position={bill.Position}, LoadingPoint={loadingPoint}, PLCLocationCode={bill.PLCLocationCode}");

                if (loadingPoint < 0 || loadingPoint > 1)
                {
                    LogService.Warning($"PLC {plc.PlcId} 自动上架装载点异常: LoadingPoint={loadingPoint}, BillID={bill.BillID}, BillNo={bill.BillNo}, ITM={bill.ITM}");
                }

                // 先记录操作日志并获取OperationID
                operationId = await LogOperationToDatabase(plc, bill, loadingPoint, 0, 1); // 初始状态为1（入库取货中）

                if (operationId == null)
                {
                    LogService.Error($"记录操作日志失败，跳过上架操作");
                    return operationId;
                }
                plc.OperationID = operationId;
                _dbService.UpdatePlcOperationId(plc.PlcId, operationId);

                // 向PLC写入上架信息
                LogService.Info($"PLC {plc.PlcId} 上架写寄存器开始: OperationID={operationId}");
                await modbusService.WriteRegisterAsync(22000, 0);     // 上架操作
                await modbusService.WriteRegisterAsync(22001, (ushort)loadingPoint);  // 装载点
                await modbusService.WriteRegisterAsync(22002, (ushort)bill.Shelf);    // 货架编号
                await modbusService.WriteRegisterAsync(22003, (ushort)bill.Position); // 储位编号
                await modbusService.WriteRegisterAsync(22008, 1);     // 启动PLC

                LogService.Info($"PLC {plc.PlcId} 上架指令已发送: 货架{bill.Shelf}, 储位{bill.Position}, 装载点{loadingPoint}, OperationID: {operationId}");
            }
            catch (Exception ex)
            {
                LogService.Error($"向PLC {plc.PlcId} 发送上架指令失败: {ex.Message}, BillID={bill?.BillID}, BillNo={bill?.BillNo}, ITM={bill?.ITM}, OperationID={operationId}, LoadingPoint={loadingPoint}");
            }
            return operationId;
        }


        // 记录操作到数据库
        private async Task<long?> LogOperationToDatabase(PlcConfiguration plc, AutoRunBill bill,
                                                 int loadingPoint, int operationType, int operationResult)
        {
            try
            {
                using (var connection = new System.Data.SqlClient.SqlConnection(AppSettings.ConnectionString))
                {
                    await connection.OpenAsync();

                    LogService.Info($"开始记录自动{GetOperationTypeName(operationType)}操作日志: PLC={plc.PlcId}, BillID={bill.BillID}, BillNo={bill.BillNo}, ITM={bill.ITM}, Tray={bill.Tray}, Shelf={bill.Shelf}, Position={bill.Position}, LoadingPoint={loadingPoint}, OperationType={operationType}, OperationResult={operationResult}");

                    // 插入LocationOperationLogs表并获取生成的ID
                    var logQuery = @"
                    INSERT INTO LocationOperationLogs 
                    (CreateTime, StartTime, PLCID, OutboundShelf, OutboundPosition, 
                     InboundShelf, InboundPosition,
                     LoadingPoint, OperationType, OperationResult)
                    OUTPUT INSERTED.ID
                    VALUES 
                    (GETDATE(), GETDATE(), @PLCID, @OutboundShelf, @OutboundPosition,
                     @InboundShelf, @InboundPosition,
                     @LoadingPoint, @OperationType, @OperationResult)";

                    // 执行插入操作并获取生成的ID
                    long operationId = await connection.ExecuteScalarAsync<long>(logQuery, new
                    {
                        PLCID = plc.PlcId,
                        OutboundShelf = operationType == 1 ? bill.Shelf : -1,
                        OutboundPosition = operationType == 1 ? bill.Position : -1,
                        InboundShelf = operationType == 0 ? bill.Shelf : -1,
                        InboundPosition = operationType == 0 ? bill.Position : -1,
                        LoadingPoint = loadingPoint,
                        OperationType = operationType,
                        OperationResult = operationResult
                    }, commandTimeout: AppSettings.DbCommandTimeout);

                    LogService.Info($"LocationOperationLogs插入成功: OperationID={operationId}, PLC={plc.PlcId}, OperationType={operationType}");

                    // 将OperationID回写到PlcConfigurations表
                    var updatePlcQuery = @"UPDATE PlcConfigurations 
                                      SET OperationID = @OperationID,
                                          UpdatedAt = GETDATE()
                                      WHERE PlcId = @PlcId";

                    await connection.ExecuteAsync(updatePlcQuery, new
                    {
                        OperationID = operationId,
                        PlcId = plc.PlcId
                    }, commandTimeout: AppSettings.DbCommandTimeout);

                    LogService.Info($"PlcConfigurations回写OperationID成功: PLC={plc.PlcId}, OperationID={operationId}");

                    // 调用存储过程更新BillDetail表
                    decimal operationWeight = ResolveOperationWeight(plc, loadingPoint);
                    await EnsureBillDetailWeightAsync(connection, bill, operationWeight);

                    if (operationType == 1)
                    {
                        LogService.Info($"自动下架业务单据时间延后到PLC完成监控中写入: BillID={bill.BillID}, BillNo={bill.BillNo}, ITM={bill.ITM}, OperationID={operationId}");
                    }
                    else
                    {
                        LogService.Info($"准备调用UPDATE_BillDetail_Operation: BillID={bill.BillID}, BillNo={bill.BillNo}, ITM={bill.ITM}, MaterialNo={bill.Tray}, OperationType={operationType}, OperationResult={operationResult}, LoadingPoint={loadingPoint}, PLC={plc.PlcId}, Weight={operationWeight}");

                        await ExecuteBillDetailOperationAsync(connection, plc, bill, operationType, operationResult, loadingPoint, operationWeight);

                        LogService.Info($"UPDATE_BillDetail_Operation调用完成，记录操作日志成功: OperationID={operationId}, OperationType={operationType}({GetOperationTypeName(operationType)})");
                    }

                    return operationId;
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"记录自动{GetOperationTypeName(operationType)}操作日志失败: {ex.Message}, PLC={plc?.PlcId}, BillID={bill?.BillID}, BillNo={bill?.BillNo}, ITM={bill?.ITM}, LoadingPoint={loadingPoint}, OperationType={operationType}, OperationResult={operationResult}");
                return null;
            }
        }

        private static decimal ResolveOperationWeight(PlcConfiguration plc, int loadingPoint)
        {
            if (plc == null)
            {
                return 0m;
            }

            decimal weight = loadingPoint == 1 ? plc.BoxWeightB : plc.BoxWeightA;
            return weight < 0m ? 0m : weight;
        }

        private static async Task EnsureBillDetailWeightAsync(System.Data.SqlClient.SqlConnection connection, AutoRunBill bill, decimal weight)
        {
            var columns = (await connection.QueryAsync<string>(@"
                SELECT c.name
                FROM sys.columns c
                WHERE c.object_id = OBJECT_ID(N'dbo.BillDetail')
                  AND c.name IN (N'BillID', N'BillNO', N'ITM', N'MaterialNo', N'Tray', N'Weight')",
                commandTimeout: AppSettings.DbCommandTimeout)).ToList();

            if (!columns.Contains("Weight") || !columns.Contains("BillID") || !columns.Contains("BillNO") || !columns.Contains("ITM"))
            {
                return;
            }

            string materialCondition = string.Empty;
            if (columns.Contains("MaterialNo"))
            {
                materialCondition = " AND MaterialNo = @MaterialNo";
            }
            else if (columns.Contains("Tray"))
            {
                materialCondition = " AND Tray = @MaterialNo";
            }

            string updateSql = $@"
                UPDATE dbo.BillDetail
                SET Weight = @Weight
                WHERE BillID = @BillID
                  AND BillNO = @BillNO
                  AND ITM = @ITM
                  AND Weight IS NULL{materialCondition}";

            int affectedRows = await connection.ExecuteAsync(updateSql, new
            {
                BillID = bill.BillID,
                BillNO = bill.BillNo,
                ITM = bill.ITM,
                MaterialNo = bill.Tray,
                Weight = weight
            }, commandTimeout: AppSettings.DbCommandTimeout);

            if (affectedRows > 0)
            {
                LogService.Info($"已补全BillDetail.Weight: BillID={bill.BillID}, BillNo={bill.BillNo}, ITM={bill.ITM}, MaterialNo={bill.Tray}, Weight={weight}");
            }
        }

        private static async Task ExecuteBillDetailOperationAsync(System.Data.SqlClient.SqlConnection connection, PlcConfiguration plc,
            AutoRunBill bill, int operationType, int operationResult, int loadingPoint, decimal operationWeight)
        {
            bool supportsWeightParameter = await connection.ExecuteScalarAsync<int>(@"
                SELECT CASE WHEN EXISTS (
                    SELECT 1
                    FROM sys.parameters
                    WHERE object_id = OBJECT_ID(N'dbo.UPDATE_BillDetail_Operation')
                      AND name = N'@Weight'
                ) THEN 1 ELSE 0 END",
                commandTimeout: AppSettings.DbCommandTimeout) == 1;

            string spQuery = supportsWeightParameter
                ? "EXEC UPDATE_BillDetail_Operation @BillID, @BillNO, @ITM, @MaterialNo, @OperationType, @OperationResult, @LoadingPoint, @PLCID, @OperationTime, @Weight"
                : "EXEC UPDATE_BillDetail_Operation @BillID, @BillNO, @ITM, @MaterialNo, @OperationType, @OperationResult, @LoadingPoint, @PLCID, @OperationTime";

            await connection.ExecuteAsync(spQuery, new
            {
                BillID = bill.BillID,
                BillNO = bill.BillNo,
                ITM = bill.ITM,
                MaterialNo = bill.Tray,
                OperationType = operationType,
                OperationResult = operationResult,
                LoadingPoint = loadingPoint,
                PLCID = plc.PlcId,
                OperationTime = DateTime.Now,
                Weight = operationWeight
            }, commandTimeout: AppSettings.DbCommandTimeout);
        }

        /// <summary>
        /// 从数据库更新PLC的AutoRun状态
        /// </summary>
        private async Task UpdatePlcAutoRunFromDatabase()
        {
            try
            {
                LogService.Info("开始更新PLC AutoRun状态");

                // 从数据库获取最新的AutoRun状态
                var latestAutoRunStatus = _dbService.GetPlcAutoRunStatus();

                // 检查每个PLC是否需要更新
                foreach (var kvp in latestAutoRunStatus)
                {
                    string plcId = kvp.Key;
                    bool newAutoRunValue = kvp.Value;

                    if (_plcAutoRunCache.TryGetValue(plcId, out bool cachedAutoRunValue))
                    {
                        // 如果值有变化，更新PLC对象
                        if (cachedAutoRunValue != newAutoRunValue)
                        {
                            await UpdatePlcAutoRunValue(plcId, newAutoRunValue);
                            _plcAutoRunCache[plcId] = newAutoRunValue;

                            LogService.Info($"PLC {plcId} AutoRun状态已更新: {cachedAutoRunValue} -> {newAutoRunValue}");
                        }
                    }
                    else
                    {
                        // 首次缓存这个PLC的AutoRun状态
                        _plcAutoRunCache[plcId] = newAutoRunValue;

                        // 如果这个PLC已经在系统中，更新其AutoRun值
                        if (_modbusServices.ContainsKey(plcId))
                        {
                            await UpdatePlcAutoRunValue(plcId, newAutoRunValue);
                            LogService.Info($"PLC {plcId} AutoRun状态已初始化: {newAutoRunValue}");
                        }
                    }
                }

                // 清理缓存中不存在的PLC
                CleanupAutoRunCache(latestAutoRunStatus);

                LogService.Info("PLC AutoRun状态更新完成");
            }
            catch (Exception ex)
            {
                LogService.Error($"更新PLC AutoRun状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新指定PLC的AutoRun值
        /// </summary>
        private async Task UpdatePlcAutoRunValue(string plcId, bool autoRunValue)
        {
            try
            {
                // 更新系统中的PLC对象
                if (_modbusServices.TryGetValue(plcId, out var modbusService))
                {
                    // 获取完整的PLC配置（异步调用）
                    var plcConfig = await _dbService.GetPlcConfigurationAsync(plcId);
                    if (plcConfig != null)
                    {
                        // 更新AutoRun属性
                        plcConfig.AutoRun = autoRunValue;

                        // 记录日志
                        LogService.Info($"PLC {plcId} AutoRun已更新为: {autoRunValue}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"更新PLC {plcId} AutoRun值失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理缓存中不存在的PLC
        /// </summary>
        private void CleanupAutoRunCache(Dictionary<string, bool> latestStatus)
        {
            var plcIdsToRemove = new List<string>();

            foreach (var cachedPlcId in _plcAutoRunCache.Keys)
            {
                if (!latestStatus.ContainsKey(cachedPlcId))
                {
                    plcIdsToRemove.Add(cachedPlcId);
                }
            }

            foreach (var plcId in plcIdsToRemove)
            {
                _plcAutoRunCache.TryRemove(plcId, out _);
                LogService.Info($"从AutoRun缓存中移除PLC: {plcId}");
            }
        }

        // 清理不再活跃的PLC服务和状态
        private void CleanupInactivePlcs(IEnumerable<PlcConfiguration> activePlcs)
        {
            try
            {
                var activePlcIds = new HashSet<string>();
                foreach (var plc in activePlcs)
                {
                    activePlcIds.Add(plc.PlcId);
                }

                var inactivePlcIds = new List<string>();
                foreach (var plcId in _modbusServices.Keys)
                {
                    if (!activePlcIds.Contains(plcId))
                    {
                        inactivePlcIds.Add(plcId);
                    }
                }

                foreach (var plcId in inactivePlcIds)
                {
                    if (_modbusServices.TryGetValue(plcId, out var service))
                    {
                        service.ConnectionStatusChanged -= OnConnectionStatusChanged;
                        service.Dispose();
                        _modbusServices.TryRemove(plcId, out _);

                        // 同时清理定时器和缓存
                        StopPlcTimer(plcId);
                        _plcAutoRunCache.TryRemove(plcId, out _);

                        LogService.Info($"清理非活跃PLC服务: {plcId}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"清理非活跃PLC时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// PLC连接状态变化事件处理
        /// </summary>
        private void OnConnectionStatusChanged(object sender, ConnectionStatusChangedEventArgs e)
        {
            try
            {
                LogService.Info($"PLC {e.PlcId} 连接状态变化: {e.Status} - {e.Message}");

                // 立即更新数据库中的连接状态
                var plc = new PlcConfiguration
                {
                    PlcId = e.PlcId,
                    LastConnectionStatus = e.Status,
                    LastErrorMessage = e.Message,
                    LastTestTime = e.Timestamp
                };

                _dbService.UpdateConnectionStatusOnly(plc);

                // 如果连接断开，更新定时器状态
                if (e.Status == "Disconnected" || e.Status == "Error")
                {
                    // 连接断开时使用正常刷新间隔
                    AdjustPlcTimerInterval(e.PlcId, false);
                    LogService.Info($"PLC {e.PlcId} 连接断开，切换为正常刷新间隔");
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"更新连接状态时发生错误: {ex.Message}");
            }
        }

        // 其他现有方法保持不变...

        /// <summary>
        /// 用于调试的入口方法
        /// </summary>
        public void DebugStart()
        {
            OnStart(null);
        }

        public void DebugStop()
        {
            OnStop();
        }
    }

    /// <summary>
    /// PLC定时器控制器（每个PLC独立）
    /// </summary>
    public class PlcTimerController : IDisposable
    {
        private readonly Timer _timer;
        private readonly string _plcId;
        private int _currentInterval;

        public event Func<string, Task> TimerElapsed;

        public int CurrentInterval => _currentInterval;

        public PlcTimerController(string plcId, int initialInterval)
        {
            _plcId = plcId;
            _currentInterval = initialInterval;

            _timer = new Timer(initialInterval);
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = true;
        }

        public void Start()
        {
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
        }

        public void AdjustInterval(int newInterval)
        {
            if (_currentInterval != newInterval)
            {
                bool wasRunning = _timer.Enabled;
                _timer.Stop();
                _timer.Interval = newInterval;
                _currentInterval = newInterval;

                if (wasRunning)
                {
                    _timer.Start();
                }
            }
        }

        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                // 异步处理，不等待结果
                _ = TimerElapsed?.Invoke(_plcId);
            }
            catch (Exception ex)
            {
                LogService.Error($"PLC {_plcId} 定时器事件处理失败: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _timer?.Stop();
            _timer?.Dispose();
        }
    }
}
