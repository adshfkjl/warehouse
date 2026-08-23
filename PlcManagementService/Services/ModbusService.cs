using System;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Net.Sockets;
using NModbus;
using NModbus.Device;
using PLCService.Configuration;
using PLCService.Models;
using PLCService.Services;
using System.Collections.Generic;
using System.Text;
using Timer = System.Timers.Timer;

namespace PLCService.Services
{
    public class ModbusService : IDisposable
    {
        private readonly PlcConfiguration _plc;
        private TcpClient _tcpClient;
        private IModbusMaster _master;
        private Timer _heartbeatTimer;
        private Timer _reconnectTimer;
        private bool _isConnected;
        private int _connectionRetryCount;
        private readonly SemaphoreSlim _modbusLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private int _isHeartbeatSending;
        private int _isReconnecting;
        private const int MaxRetryCount = 5;
        private const int ReconnectInterval = 60000; // 1分钟
        
        // 添加字段存储地址偏移量
        private Dictionary<ushort, int> _registerOffsets = new Dictionary<ushort, int>();
        private ushort _startAddress;
        private ushort _registerCount;

        public event EventHandler<ConnectionStatusChangedEventArgs> ConnectionStatusChanged;

        public ModbusService(PlcConfiguration plc)
        {
            _plc = plc;

            // 初始化寄存器偏移量
            InitializeRegisterOffsets();

            // 心跳定时器
            _heartbeatTimer = new Timer(1000);
            _heartbeatTimer.Elapsed += async (s, e) => await SendHeartbeat();
            _heartbeatTimer.AutoReset = true;

            // 重连定时器
            _reconnectTimer = new Timer(ReconnectInterval);
            _reconnectTimer.Elapsed += async (s, e) => await TryReconnect();
            _reconnectTimer.AutoReset = true;
        }

        public bool IsConnected => _isConnected;

        public async Task<bool> ConnectAsync()
        {
            await _connectLock.WaitAsync();
            try
            {
                if (_isConnected)
                {
                    return true;
                }

                var tcpClient = new TcpClient();
                var connectTask = tcpClient.ConnectAsync(_plc.IpAddress, _plc.Port);
                var timeoutTask = Task.Delay(AppSettings.PlcConnectTimeout);
                if (await Task.WhenAny(connectTask, timeoutTask) != connectTask)
                {
                    tcpClient.Close();
                    throw new TimeoutException($"连接PLC超时({AppSettings.PlcConnectTimeout}ms)");
                }

                await connectTask;

                _master?.Dispose();
                _tcpClient?.Close();
                _tcpClient = tcpClient;

                var factory = new ModbusFactory();
                _master = factory.CreateMaster(_tcpClient);

                _master.Transport.Retries = 3;
                _master.Transport.ReadTimeout = 1500;
                _master.Transport.WriteTimeout = 1500;

                _isConnected = true;
                _connectionRetryCount = 0;
                _heartbeatTimer.Start();
                _reconnectTimer.Stop();

                UpdateConnectionStatus("Connected", "连接成功");
                LogService.Info($"成功连接到PLC: {_plc.PlcId} ({_plc.IpAddress}:{_plc.Port})");

                return true;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                _connectionRetryCount++;

                UpdateConnectionStatus("Disconnected", $"连接失败: {ex.Message}");
                LogNetworkException("连接PLC", ex);
                LogService.Error($"连接PLC失败: {_plc.PlcId}, 错误: {ex.Message}, 重试次数: {_connectionRetryCount}");

                // 启动重连定时器
                if (_connectionRetryCount < MaxRetryCount)
                {
                    _reconnectTimer.Start();
                }
                else
                {
                    UpdateConnectionStatus("Failed", $"连接失败超过最大重试次数({MaxRetryCount})");
                    LogService.Warning($"PLC {_plc.PlcId} 连接失败超过最大重试次数，停止重试");
                }

                return false;
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private async Task TryReconnect()
        {
            if (_isConnected) return;
            if (Interlocked.Exchange(ref _isReconnecting, 1) == 1) return;

            try
            {
                LogService.Info($"尝试重新连接PLC: {_plc.PlcId}, 重试次数: {_connectionRetryCount + 1}");
                await ConnectAsync();
            }
            finally
            {
                Interlocked.Exchange(ref _isReconnecting, 0);
            }
        }

        /// <summary>
        /// 初始化寄存器偏移量映射
        /// </summary>
        private void InitializeRegisterOffsets()
        {
            // 计算需要读取的起始地址和数量
            ushort minAddress = ushort.MaxValue;
            ushort maxAddress = ushort.MinValue;

            // 所有需要读取的地址列表
            var addressList = new List<ushort>
        {
            GetRegisterAddress(ModbusAddress.PlcStatus, _plc),
            GetRegisterAddress(ModbusAddress.WorkStatus, _plc),
            GetRegisterAddress(ModbusAddress.TaskResponse, _plc),
            GetRegisterAddress(ModbusAddress.ForksStatus, _plc),
            GetRegisterAddress(ModbusAddress.ResetCompleted, _plc),
            GetRegisterAddress(ModbusAddress.InboundCompleted, _plc),
            GetRegisterAddress(ModbusAddress.OutboundCompleted, _plc),
            GetRegisterAddress(ModbusAddress.RelocationCompleted, _plc),
            GetRegisterAddress(ModbusAddress.AlarmInfo, _plc),
            GetRegisterAddress(ModbusAddress.PosX, _plc),
            GetRegisterAddress(ModbusAddress.PosY, _plc),
            GetRegisterAddress(ModbusAddress.PosZ, _plc),
            GetRegisterAddress(ModbusAddress.SpeedX, _plc),
            GetRegisterAddress(ModbusAddress.SpeedY, _plc),
            GetRegisterAddress(ModbusAddress.BoxWeight1, _plc),
            GetRegisterAddress(ModbusAddress.BoxWeight2, _plc)
        };

            // 找到最小和最大地址
            foreach (var addr in addressList)
            {
                if (addr < minAddress) minAddress = addr;
                if (addr > maxAddress) maxAddress = addr;

                // 对于需要2个寄存器的地址，考虑下一个地址
                var addrPlusOne = (ushort)(addr + 1);
                if (addrPlusOne > maxAddress) maxAddress = addrPlusOne;
            }

            _startAddress = minAddress;
            _registerCount = (ushort)(maxAddress - minAddress + 1);

            // 计算每个地址的偏移量
            foreach (var addr in addressList)
            {
                _registerOffsets[addr] = addr - _startAddress;
            }
        }



        public async Task<PlcConfiguration> ReadPlcDataAsync()
        {
            if (!_isConnected)
            {
                _plc.LastConnectionStatus = "Disconnected";
                _plc.LastTestTime = DateTime.Now;
                return _plc;
            }

            return await ReadPlcDataCoreAsync();
        }

        private PlcConfiguration HandleReadFailure(Exception ex)
        {
            _isConnected = false;
            _heartbeatTimer.Stop();
            _reconnectTimer.Start();

            UpdateConnectionStatus("Error", $"读取数据失败: {ex.Message}");
            LogNetworkException("读取PLC数据", ex);
            LogService.Error($"读取PLC数据失败: {_plc.PlcId}, 错误: {ex.Message}");

            return _plc;
        }

        private async Task<PlcConfiguration> ReadPlcDataCoreAsync()
        {
            try
            {
                await _modbusLock.WaitAsync();
                try
                {
                    ushort[] allRegisters = await _master.ReadHoldingRegistersAsync(_plc.SlaveId, _startAddress, _registerCount);
                    ParsePlcDataWithPrecomputedOffsets(allRegisters);
                    _plc.LastTestTime = DateTime.Now;
                    _plc.LastErrorMessage = null;
                    return _plc;
                }
                finally
                {
                    _modbusLock.Release();
                }
            }
            catch (Exception ex)
            {
                return HandleReadFailure(ex);
            }
        }

        /// <summary>
        /// 使用预计算偏移量解析PLC数据
        /// </summary>
        private void ParsePlcDataWithPrecomputedOffsets(ushort[] allRegisters)
        {
            if (allRegisters == null || allRegisters.Length == 0)
                return;

            try
            {
                // 1. PLC状态
                if (TryGetRegisterValue(ModbusAddress.PlcStatus, allRegisters, out ushort statusValue))
                {
                    _plc.LastConnectionStatus = statusValue == 1 ? "联机" : "脱机";
                }

                // 2. 工作状态
                if (TryGetRegisterValue(ModbusAddress.WorkStatus, allRegisters, out ushort workStatusValue))
                {
                    _plc.IsEmergencyStop = workStatusValue == 4;
                }

                // 3. 任务应答
                if (TryGetRegisterValue(ModbusAddress.TaskResponse, allRegisters, out ushort taskResponseValue))
                {
                    _plc.OperationResult = taskResponseValue;
                }

                // 4. 货叉状态
                if (TryGetRegisterValue(ModbusAddress.ForksStatus, allRegisters, out ushort forksStatusValue))
                {
                    _plc.ForksStat = forksStatusValue == 1;
                }

                // 5. 复位完成状态
                if (TryGetRegisterValue(ModbusAddress.ResetCompleted, allRegisters, out ushort resetCompletedValue))
                {
                    _plc.IsResetCompleted = resetCompletedValue == 1;
                }

                // 6. 入库完成状态
                if (TryGetRegisterValue(ModbusAddress.InboundCompleted, allRegisters, out ushort inboundCompletedValue))
                {
                    _plc.IsInboundCompleted = inboundCompletedValue == 1;
                }

                // 7. 出库完成状态
                if (TryGetRegisterValue(ModbusAddress.OutboundCompleted, allRegisters, out ushort outboundCompletedValue))
                {
                    _plc.IsOutboundCompleted = outboundCompletedValue == 1;
                }

                // 8. 移库完成状态
                if (TryGetRegisterValue(ModbusAddress.RelocationCompleted, allRegisters, out ushort relocationCompletedValue))
                {
                    _plc.IsRelocationCompleted = relocationCompletedValue == 1;
                }

                // 9. 报警信息 (需要2个寄存器)
                if (TryGetRegisters(ModbusAddress.AlarmInfo, allRegisters, 2, out ushort[] alarmRegs))
                {
                    int alarmCode = (alarmRegs[1] << 16) | alarmRegs[0];
                    _plc.LastErrorMessage = AlarmDecoder.DecodeAlarmToString(alarmCode);
                }

                // 10. 位置信息 X (需要2个寄存器)
                if (TryGetRegisters(ModbusAddress.PosX, allRegisters, 2, out ushort[] posXRegs))
                {
                    _plc.PosX = (decimal)ConvertToFloat(posXRegs);
                }

                // 11. 位置信息 Y (需要2个寄存器)
                if (TryGetRegisters(ModbusAddress.PosY, allRegisters, 2, out ushort[] posYRegs))
                {
                    _plc.PosY = (decimal)ConvertToFloat(posYRegs);
                }

                // 12. 位置信息 Z (需要2个寄存器)
                if (TryGetRegisters(ModbusAddress.PosZ, allRegisters, 2, out ushort[] posZRegs))
                {
                    _plc.PosZ = (decimal)ConvertToFloat(posZRegs);
                }

                // 13. 速度信息 X (需要2个寄存器)
                if (TryGetRegisters(ModbusAddress.SpeedX, allRegisters, 2, out ushort[] speedXRegs))
                {
                    _plc.AuxSpeed = Convert.ToInt32(ConvertToFloat(speedXRegs));
                }

                // 14. 速度信息 Y (需要2个寄存器)
                if (TryGetRegisters(ModbusAddress.SpeedY, allRegisters, 2, out ushort[] speedYRegs))
                {
                    _plc.MainSpeed = Convert.ToInt32(ConvertToFloat(speedYRegs));
                }

                // 15. 箱子重量1
                if (TryGetRegisterValue(ModbusAddress.BoxWeight1, allRegisters, out ushort weight1Value))
                {
                    _plc.BoxWeightA = Convert.ToDecimal(Convert.ToInt32((short)weight1Value) / 100.00);
                }

                // 16. 箱子重量2
                if (TryGetRegisterValue(ModbusAddress.BoxWeight2, allRegisters, out ushort weight2Value))
                {
                    _plc.BoxWeightB = Convert.ToDecimal(Convert.ToInt32((short)weight2Value) / 100.00);
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"解析PLC寄存器数据失败: {_plc.PlcId}, 错误: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 获取单个寄存器的值
        /// </summary>
        private bool TryGetRegisterValue(ushort address, ushort[] allRegisters, out ushort value)
        {
            value = 0;

            var actualAddress = GetRegisterAddress(address, _plc);
            if (_registerOffsets.TryGetValue(actualAddress, out int offset) &&
                offset >= 0 && offset < allRegisters.Length)
            {
                value = allRegisters[offset];
                return true;
            }

            return false;
        }

        /// <summary>
        /// 获取多个寄存器的值
        /// </summary>
        private bool TryGetRegisters(ushort address, ushort[] allRegisters, int count, out ushort[] values)
        {
            values = new ushort[count];

            var actualAddress = GetRegisterAddress(address, _plc);
            if (_registerOffsets.TryGetValue(actualAddress, out int offset) &&
                offset >= 0 && offset + count - 1 < allRegisters.Length)
            {
                Array.Copy(allRegisters, offset, values, 0, count);
                return true;
            }

            return false;
        }


        //public async Task<PlcConfiguration> ReadPlcDataAsync()
        //{
        //    if (!_isConnected)
        //    {
        //        _plc.LastConnectionStatus = "Disconnected";
        //        _plc.LastTestTime = DateTime.Now;
        //        return _plc;
        //    }
        //    ushort RegAddress=0;
        //    try
        //    {
        //        // 读取PLC状态信息
        //        RegAddress = GetRegisterAddress(ModbusAddress.PlcStatus, _plc);
        //        var status = await _master.ReadHoldingRegistersAsync(_plc.SlaveId,RegAddress, 1);
        //        _plc.LastConnectionStatus = status[0] == 1 ? "Online" : "Offline";

        //        // 读取工作状态
        //        RegAddress = GetRegisterAddress(ModbusAddress.WorkStatus, _plc);
        //        var workStatus = await _master.ReadHoldingRegistersAsync(_plc.SlaveId,RegAddress, 1);
        //        _plc.IsEmergencyStop = workStatus[0] == 4;

        //        // 读取任务应答
        //        RegAddress = GetRegisterAddress(ModbusAddress.TaskResponse, _plc);
        //        var taskResponse = await _master.ReadHoldingRegistersAsync(_plc.SlaveId, RegAddress , 1);
        //        _plc.OperationResult = taskResponse[0];

        //        // 读取货叉状态
        //        RegAddress = GetRegisterAddress(ModbusAddress.ForksStatus, _plc);
        //        var forksStatus = await _master.ReadHoldingRegistersAsync(_plc.SlaveId,RegAddress, 1);
        //        _plc.ForksStat = forksStatus[0] == 1;

        //        // 读取完成状态
        //        RegAddress = GetRegisterAddress(ModbusAddress.ResetCompleted, _plc);
        //        var resetCompleted = await _master.ReadHoldingRegistersAsync(_plc.SlaveId,RegAddress , 1);
        //        _plc.IsResetCompleted = resetCompleted[0] == 1;

        //        RegAddress = GetRegisterAddress(ModbusAddress.InboundCompleted, _plc);
        //        var inboundCompleted = await _master.ReadHoldingRegistersAsync(_plc.SlaveId, RegAddress, 1);
        //        _plc.IsInboundCompleted = inboundCompleted[0] == 1;

        //        RegAddress = GetRegisterAddress(ModbusAddress.OutboundCompleted, _plc);
        //        var outboundCompleted = await _master.ReadHoldingRegistersAsync(_plc.SlaveId, RegAddress, 1);
        //        _plc.IsOutboundCompleted = outboundCompleted[0] == 1;

        //        RegAddress = GetRegisterAddress(ModbusAddress.RelocationCompleted, _plc);
        //        var relocationCompleted = await _master.ReadHoldingRegistersAsync(_plc.SlaveId, RegAddress , 1);
        //        _plc.IsRelocationCompleted = relocationCompleted[0] == 1;

        //        RegAddress =  GetRegisterAddress(ModbusAddress.AlarmInfo, _plc);
        //        var alarmCode = await _master.ReadHoldingRegistersAsync(_plc.SlaveId, RegAddress, 2);

        //        _plc.LastErrorMessage = AlarmDecoder.DecodeAlarmToString(Convert.ToInt32((alarmCode[1] << 16) | alarmCode[0]));


        //        // 读取位置信息 (浮点数需要读取2个寄存器)
        //        RegAddress =  GetRegisterAddress(ModbusAddress.PosX, _plc);
        //        var posX = await _master.ReadHoldingRegistersAsync(_plc.SlaveId, RegAddress, 2);
        //        _plc.PosX = (decimal)ConvertToFloat(posX);

        //        RegAddress =  GetRegisterAddress(ModbusAddress.PosY, _plc);
        //        var posY = await _master.ReadHoldingRegistersAsync(_plc.SlaveId, RegAddress, 2);
        //        _plc.PosY = (decimal)ConvertToFloat(posY);

        //        RegAddress = GetRegisterAddress(ModbusAddress.PosZ, _plc);
        //        var posZ = await _master.ReadHoldingRegistersAsync(_plc.SlaveId,RegAddress , 2);
        //        _plc.PosZ = (decimal)ConvertToFloat(posZ);

        //        // 读取速度信息
        //        RegAddress = GetRegisterAddress(ModbusAddress.SpeedY, _plc);
        //        var speedY = await _master.ReadHoldingRegistersAsync(_plc.SlaveId,RegAddress , 2);
        //        _plc.MainSpeed = Convert.ToInt32(ConvertToFloat(speedY));

        //        RegAddress = GetRegisterAddress(ModbusAddress.SpeedX, _plc);
        //        var speedX = await _master.ReadHoldingRegistersAsync(_plc.SlaveId, RegAddress, 2);
        //        _plc.AuxSpeed = Convert.ToInt32(ConvertToFloat(speedX));

        //        RegAddress = GetRegisterAddress(ModbusAddress.BoxWeight1, _plc);
        //        ushort[] weight1 = await _master.ReadHoldingRegistersAsync(_plc.SlaveId,RegAddress , 1); 
        //        //await _master.ReadHoldingRegistersAsync(_plc.SlaveId, ModbusAddress.BoxWeight1, 2);

        //        _plc.BoxWeightA = Convert.ToDecimal(Convert.ToInt32((short)weight1[0])/100.00)  ;//  Convert.ToDecimal(ConvertToFloat(weight1));

        //        RegAddress = GetRegisterAddress(ModbusAddress.BoxWeight2, _plc);
        //        ushort[] weight2 = await _master.ReadHoldingRegistersAsync(_plc.SlaveId,RegAddress , 1); 
        //        //await _master.ReadHoldingRegistersAsync(_plc.SlaveId, ModbusAddress.BoxWeight1, 2);
        //        _plc.BoxWeightB = Convert.ToDecimal(Convert.ToInt32((short)weight2[0])/100.00)  ;//  Convert.ToDecimal(ConvertToFloat(weight1));

        //        //ushort[] weight2 = await _master.ReadHoldingRegistersAsync(_plc.SlaveId, ModbusAddress.BoxWeight2, 2);
        //        //_plc.BoxWeightB = Convert.ToDecimal(ConvertToFloat(weight2));

        //        _plc.LastTestTime = DateTime.Now;
        //        _plc.LastErrorMessage = null;

        //        return _plc;
        //    }
        //    catch (Exception ex)
        //    {
        //        _isConnected = false;
        //        _heartbeatTimer.Stop();
        //        _reconnectTimer.Start();

        //        UpdateConnectionStatus("Error", $"读取数据失败: {ex.Message}");
        //        LogService.Error($"读取PLC数据失败: {_plc.PlcId}, 错误: {ex.Message}{ex.Source},寄存器地址：{ RegAddress }");

        //        return _plc;
        //    }
        //}

        private ushort GetRegisterAddress(int Address, PlcConfiguration Plc)
        {

            return (ushort)(Address + Plc.RegisterAddrOffset);

        }
        private async Task SendHeartbeat()
        {
            if (!_isConnected) return;
            if (Interlocked.Exchange(ref _isHeartbeatSending, 1) == 1) return;

            try
            {
                var heartbeatValue = (ushort)(DateTime.Now.Second % 2);
                await _modbusLock.WaitAsync();
                try
                {
                await _master.WriteSingleRegisterAsync(_plc.SlaveId, ModbusAddress.Heartbeat, heartbeatValue);
                }
                finally
                {
                    _modbusLock.Release();
                }
            }
            catch (Exception ex)
            {
                _isConnected = false;
                _heartbeatTimer.Stop();
                _reconnectTimer.Start();

                UpdateConnectionStatus("Error", $"心跳发送失败: {ex.Message}");
                LogNetworkException("发送心跳", ex);
                LogService.Error($"发送心跳失败: {_plc.PlcId}, 错误: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _isHeartbeatSending, 0);
            }
        }
        public async Task AutoRunTask(string PlcID)
        {
            if (!_isConnected) return;

            try
            {
                var heartbeatValue = (ushort)(DateTime.Now.Second % 2);
                await _modbusLock.WaitAsync();
                try
                {
                    await _master.WriteSingleRegisterAsync(_plc.SlaveId, ModbusAddress.Heartbeat, heartbeatValue);
                }
                finally
                {
                    _modbusLock.Release();
                }
            }
            catch (Exception ex)
            {
                _isConnected = false;
                _heartbeatTimer.Stop();
                _reconnectTimer.Start();

                UpdateConnectionStatus("Error", $"心跳发送失败: {ex.Message}");
                LogNetworkException("自动任务心跳", ex);
                LogService.Error($"发送心跳失败: {_plc.PlcId}, 错误: {ex.Message}");
            }
        }

        private void LogNetworkException(string operation, Exception ex)
        {
            LogService.Error($"**** PLC网络异常 **** PLC={_plc.PlcId}, IP={_plc.IpAddress}, Port={_plc.Port}, 操作={operation}, 错误={ex.Message}");
        }

        private void UpdateConnectionStatus(string status, string message)
        {
            _plc.LastConnectionStatus = status;
            _plc.LastErrorMessage = message;
            _plc.LastTestTime = DateTime.Now;

            ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs
            {
                PlcId = _plc.PlcId,
                Status = status,
                Message = message,
                Timestamp = DateTime.Now
            });
        }

        // 浮点数转换方法和其他辅助方法保持不变
        private float ConvertToFloat(ushort[] registers)
        {
            if (registers.Length != 2) return 0;

            // 读取重量信息
            int intValue = (registers[1] << 16) | registers[0];
            // 将32位整数转换为浮点数
            byte[] bytes = BitConverter.GetBytes(intValue);
            return BitConverter.ToSingle(bytes, 0);
        }

        public void Dispose()
        {
            _heartbeatTimer?.Stop();
            _heartbeatTimer?.Dispose();
            _reconnectTimer?.Stop();
            _reconnectTimer?.Dispose();
            _master?.Dispose();
            _tcpClient?.Close();
            _modbusLock?.Dispose();
            _connectLock?.Dispose();
        }

        // 添加写单个寄存器的方法
        public async Task WriteRegisterAsync(ushort address, ushort value)
        {
            if (!_isConnected)
                throw new InvalidOperationException("PLC未连接");

            try
            {
                // 考虑寄存器地址偏移量
                ushort actualAddress = (ushort)(address + _plc.RegisterAddrOffset);
                await _modbusLock.WaitAsync();
                try
                {
                    await _master.WriteSingleRegisterAsync(_plc.SlaveId, actualAddress, value);
                }
                finally
                {
                    _modbusLock.Release();
                }

                LogService.Info($"向PLC {_plc.PlcId} 地址 {address}(实际{actualAddress}) 写入值 {value}");
            }
            catch (Exception ex)
            {
                LogNetworkException($"写单个寄存器 地址={address}", ex);
                LogService.Error($"向PLC {_plc.PlcId} 写寄存器失败: {ex.Message}");
                throw;
            }
        }

        // 批量写入寄存器
        public async Task WriteRegistersAsync(ushort startAddress, ushort[] values)
        {
            if (!_isConnected)
                throw new InvalidOperationException("PLC未连接");

            try
            {
                // 考虑寄存器地址偏移量
                ushort actualAddress = (ushort)(startAddress + _plc.RegisterAddrOffset);
                await _modbusLock.WaitAsync();
                try
                {
                    await _master.WriteMultipleRegistersAsync(_plc.SlaveId, actualAddress, values);
                }
                finally
                {
                    _modbusLock.Release();
                }

                LogService.Info($"向PLC {_plc.PlcId} 地址 {startAddress} 批量写入 {values.Length} 个值");
            }
            catch (Exception ex)
            {
                LogNetworkException($"批量写寄存器 起始地址={startAddress}", ex);
                LogService.Error($"向PLC {_plc.PlcId} 批量写寄存器失败: {ex.Message}");
                throw;
            }
        }

    }

    public class ConnectionStatusChangedEventArgs : EventArgs
    {
        public string PlcId { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; }
    }



    public class AlarmDecoder
    {
        // 报警位定义
        private static readonly Dictionary<int, string> AlarmDefinitions = new Dictionary<int, string>
        {
            {1,"入库时原货架尚无货"},
            {2,"入库时未能取到货"},
            {3,"入库时目标货架已有货"},
            {4,"入库时未能成功放货"},
            {5,"出库时原货架尚无货"},
            {6,"出库时未能取到货"},
            {7,"出库时目标货架已有货"},
            {8,"出库时未能成功放货"},
            {9,"盘库时原货架尚无货"},
            {10,"盘库时未能取到货"},
            {11,"盘库时目标货架已有货"},
            {12,"盘库时未能成功放货"},
            {13,"已装载货物"},
            {14,"手动中禁止自动"},
            {15,"自动中禁止手动"},
            {16,"需要复位"},
            {17,"碰到前限位"},
            {18,"碰到后限位"},
            {19,"碰到上限位"},
            {20,"碰到下限位"},
            {21,"碰到A限位"},
            {22,"碰到B限位"},
            {23,"货位超范围"},
            {24,"未启动"},
            {25,"箱1过重"},
            {26,"箱2过重"},
            {27,"上位机通讯超时"}
        };


        public static string DecodeAlarmToString(int alarmCode)
        {
            if (alarmCode == 0)
                return "状态正常";

            StringBuilder alarmString = new StringBuilder();

            // 检查每一位是否被设置
            for (int i = 1; i <= 32; i++)
            {
                if ((alarmCode & (1 << (i - 1))) != 0)
                {
                    if (AlarmDefinitions.TryGetValue(i, out string alarmDescription))
                    {
                        if (alarmString.Length > 0)
                        {
                            alarmString.Append("：");
                        }
                        alarmString.Append(alarmDescription);
                    }
                }
            }

            return alarmString.Length > 0 ? alarmString.ToString() : "状态正常";
        }
    }
}

