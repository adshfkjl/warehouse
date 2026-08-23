// PLCManagement.Core/Services/ModbusClient.cs
using NModbus;
using NModbus.Device;
using System.Net.Sockets;
using PLCManagement.Core.Interfaces;
using PLCManagement.Core.Models;

namespace PLCManagement.Core.Services
{
    public class ModbusClient : IModbusClient, IDisposable
    {
        private TcpClient _tcpClient;
        private IModbusMaster _modbusMaster;
        private byte _slaveId;
        private string _ipAddress;
        private int _port;
        private bool _isConnected;
        private readonly ILogger _logger;
        private int _registerAddressOffset;
        private readonly object _syncRoot = new object();
        private int _timeoutMilliseconds = 3000;
        private readonly bool _suppressConnectionFailureLogging;


        // 实现接口要求的Logger属性
        public ILogger Logger { get; set; }
        public bool IsConnected
        {
            get
            {
                try
                {
                    // 更可靠的连接状态检查
                    return _isConnected && _tcpClient?.Connected == true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void SetTimeout(int milliseconds)
        {
            try
            {
                _timeoutMilliseconds = milliseconds;
                if (_tcpClient == null) return;

                _tcpClient.SendTimeout = milliseconds;
                _tcpClient.ReceiveTimeout = milliseconds;
                if (_modbusMaster != null)
                {
                    _modbusMaster.Transport.ReadTimeout = milliseconds;
                    _modbusMaster.Transport.WriteTimeout = milliseconds;
                }
            }
            catch (Exception ex)
            {
                // 记录错误但不中断
                Console.WriteLine($"设置超时失败: {ex.Message}");
            }
        }
        public ModbusClient(string ipAddress, int port, byte slaveId, int registerAddressOffset = 0, ILogger logger = null, bool suppressConnectionFailureLogging = false)
        {
            _ipAddress = ipAddress;
            _port = port;
            _slaveId = slaveId;
            _logger = logger;
            _registerAddressOffset = registerAddressOffset;
            _suppressConnectionFailureLogging = suppressConnectionFailureLogging;
        }

        public ModbusResponse Connect()
        {
            lock (_syncRoot)
            {
            try
            {
                _logger?.LogInformation($"尝试连接PLC：{_ipAddress}:{_port}");
                if (IsConnected)
                {
                    return new ModbusResponse { IsSuccess = true, Message = "Already connected" };
                }

                CloseConnection();

                var tcpClient = new TcpClient
                {
                    SendTimeout = _timeoutMilliseconds,
                    ReceiveTimeout = _timeoutMilliseconds
                };

                var connectTask = tcpClient.ConnectAsync(_ipAddress, _port);
                if (!connectTask.Wait(_timeoutMilliseconds))
                {
                    tcpClient.Close();
                    throw new TimeoutException($"连接PLC超时({_timeoutMilliseconds}ms)");
                }

                connectTask.GetAwaiter().GetResult();
                _tcpClient = tcpClient;

                var factory = new ModbusFactory();
                _modbusMaster = factory.CreateMaster(_tcpClient);
                _modbusMaster.Transport.ReadTimeout = _timeoutMilliseconds;
                _modbusMaster.Transport.WriteTimeout = _timeoutMilliseconds;
                _modbusMaster.Transport.Retries = 0;

                _isConnected = true;
                return new ModbusResponse { IsSuccess = true, Message = "Connected successfully" };
            }
            catch (Exception ex)
            {
                _isConnected = false;
                if (!_suppressConnectionFailureLogging)
                {
                    LogNetworkException("连接PLC", ex);
                    _logger?.LogError(ex, "PLC连接失败");
                }
                return new ModbusResponse
                {
                    IsSuccess = false,
                    Message = $"Connection failed: {ex.Message}"
                };
            }
            }
        }

        public bool Reconnect()
        {
            try
            {
                Disconnect();
                return Connect().IsSuccess;
            }
            catch
            {
                return false;
            }
        }

        public void Disconnect()
        {
            lock (_syncRoot)
            {
                CloseConnection();
            }
        }

        private void CloseConnection()
        {
            _isConnected = false;
            try { _modbusMaster?.Dispose(); } catch { }
            try { _tcpClient?.Close(); } catch { }
            try { _tcpClient?.Dispose(); } catch { }
            _modbusMaster = null;
            _tcpClient = null;
        }

        // 修改 WriteSingleRegister 方法，添加重试逻辑
        public ModbusResponse WriteSingleRegister(ushort address, ushort value, int maxRetries = 1)
        {
            lock (_syncRoot)
            {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (!EnsureConnected().IsSuccess)
                    {
                        return new ModbusResponse { IsSuccess = false, Message = "PLC连接失败" };
                    }

                    // 原有的写入逻辑
                    _modbusMaster.WriteSingleRegister(_slaveId, GetRegisterAddress(address), value);

                    return new ModbusResponse { IsSuccess = true, Message = "写入成功" };

                    // _tcpClient.WriteSingleRegisterInternal(address, value);
                }
                catch (IOException ex) when (attempt < maxRetries)
                {
                    LogNetworkException($"写单个寄存器重试 地址={address}", ex);
                    // 网络异常，尝试重连
                    CloseConnection();
                    EnsureConnected();
                }
                catch (SocketException ex) when (attempt < maxRetries)
                {
                    LogNetworkException($"写单个寄存器重试 地址={address}", ex);
                    CloseConnection();
                    EnsureConnected();
                }
                catch (TimeoutException ex) when (attempt < maxRetries)
                {
                    LogNetworkException($"写单个寄存器重试 地址={address}", ex);
                    CloseConnection();
                    EnsureConnected();
                }
                catch (Exception ex)
                {
                    _isConnected = false;
                    LogNetworkException($"写单个寄存器 地址={address}", ex);
                    return new ModbusResponse
                    {
                        IsSuccess = false,
                        Message = ex.Message
                    };
                }
            }

            return new ModbusResponse
            {
                IsSuccess = false,
                Message = "写入失败，超过最大重试次数"
            };
            }
        }

        public ModbusResponse WriteMultipleRegisters(ushort startAddress, ushort[] values)
        {
            lock (_syncRoot)
            {
                var connectResult = EnsureConnected();
                if (!connectResult.IsSuccess)
                {
                    return connectResult;
                }

                try
                {
                    _modbusMaster.WriteMultipleRegisters(_slaveId, GetRegisterAddress(startAddress), values);
                    return new ModbusResponse { IsSuccess = true, Message = "Write successful" };
                }
                catch (Exception ex)
                {
                    _isConnected = false;
                    LogNetworkException($"批量写寄存器 起始地址={startAddress}", ex);
                    return new ModbusResponse
                    {
                        IsSuccess = false,
                        Message = $"Write failed: {ex.Message}"
                    };
                }
            }
        }

        public ModbusResponse<ushort[]> ReadHoldingRegisters(ushort startAddress, ushort numberOfPoints)
        {
            lock (_syncRoot)
            {
                var connectResult = EnsureConnected();
                if (!connectResult.IsSuccess)
                {
                    return new ModbusResponse<ushort[]>
                    {
                        IsSuccess = false,
                        Message = connectResult.Message
                    };
                }

                try
                {
                    var registers = _modbusMaster.ReadHoldingRegisters(_slaveId, GetRegisterAddress(startAddress), numberOfPoints);
                    return new ModbusResponse<ushort[]>
                    {
                        IsSuccess = true,
                        Message = "Read successful",
                        Data = registers
                    };
                }
                catch (Exception ex)
                {
                    _isConnected = false;
                    LogNetworkException($"读取寄存器 起始地址={startAddress}", ex);
                    return new ModbusResponse<ushort[]>
                    {
                        IsSuccess = false,
                        Message = $"Read failed: {ex.Message}"
                    };
                }
            }
        }

        // 添加一个真正测试连接的方法
        public bool TestConnection()
        {
            try
            {
                // 尝试读取一个寄存器来测试连接
                var result = ReadHoldingRegisters(23000, 1);
                return result.IsSuccess;
            }
            catch
            {
                return false;
            }
        }


        public void Dispose()
        {
            Disconnect();
        }

        private ModbusResponse EnsureConnected()
        {
            if (IsConnected && _modbusMaster != null)
            {
                return new ModbusResponse { IsSuccess = true, Message = "Already connected" };
            }

            return Connect();
        }

        private ushort GetRegisterAddress(int Address)
        {
            return (ushort)(Address + this._registerAddressOffset);
        }

        private void LogNetworkException(string operation, Exception? ex)
        {
            var message = $"**** PLC网络异常 **** IP={_ipAddress}, Port={_port}, 操作={operation}, 错误={ex?.Message ?? "连接中断或超时"}";
            _logger?.LogError(ex, message);
            Logger?.LogError(ex, message);
        }
    }
}
