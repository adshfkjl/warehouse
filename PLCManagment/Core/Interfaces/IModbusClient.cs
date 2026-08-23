using PLCManagement.Core.Models;
using Microsoft.Extensions.Logging;

namespace PLCManagement.Core.Interfaces
{
    public interface IModbusClient : IDisposable
    {
        bool IsConnected { get; }
        bool TestConnection();

        ModbusResponse Connect();
        bool Reconnect();

        ILogger Logger { get; set; }  // 添加日志接口
        void Disconnect();
        ModbusResponse WriteSingleRegister(ushort startAddress, ushort value,int maxRetries);
        ModbusResponse WriteMultipleRegisters(ushort startAddress, ushort[] values);
        ModbusResponse<ushort[]> ReadHoldingRegisters(ushort startAddress, ushort numberOfPoints);
        //ModbusResponse WriteSingleRegister(ushort address, ushort value);

    }
}
