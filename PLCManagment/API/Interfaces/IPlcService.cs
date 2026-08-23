// PLCManagement.API/Interfaces/IPlcService.cs
using PLCManagement.API.Models;
using PLCManagement.API.Models.Dtos;
using PLCManagement.Core.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PLCManagement.API.Interfaces
{
    public interface IPlcService
    {
        Task InitializePlcConnections();
        Task<PlcConfiguration> GetPlcConfiguration(string plcId);
        Task<List<PlcConfiguration>> GetAllPlcConfigurations();
        Task<PlcConfiguration> AddPlcConfiguration(PlcConfiguration plc);
        Task UpdatePlcConfiguration(string plcId, PlcConfiguration plc);
        Task DeletePlcConfiguration(string plcId);
        Task<ModbusResponse> TestPlcConnection(string plcId);
        Task<ModbusResponse> WriteToPlc(string plcId, ushort startAddress, string value, int dataType);
        Task<ModbusResponse<ushort[]>> ReadFromPlc(string plcId, ushort startAddress, ushort numberOfPoints);
        Task<ModbusResponse> OutboundOperation(string plcId, int outShelf, int selectedOutPosition, int loadingPoint);
        Task<ModbusResponse> OutboundOperation(string plcId, int outShelf, int selectedOutPosition, int loadingPoint, string billI, string billNo, int itm);
        Task<ModbusResponse> InboundOperation(string plcId, int inShelf, int selectedInPosition, int loadingPoint, string billI, string billNo, int itm, double qty,string rem);

        Task<ModbusResponse> InboundOperation(string plcId, int inShelf, int selectedInPosition, int loadingPoint);
        Task<ModbusResponse> TransferOperation(string plcId, int outShelf, int selectedOutPosition, int inShelf, int selectedInPosition);
        Task<ModbusResponse> SysSettingOperation(string plcId, int maxSpeed, int auxSpeed, decimal forkLength);
        Task<PlcStatusDto> ReadPlcStatusAsync(string plcId);
        Task<PlcConfiguration> UpdatePlcStatusAsync(string plcId);
        //Task<bool> ValidateOperation(string plcId, int shelf, int position, int loadingPoint, int operationType);
        //Task ValidateAllConnections();
    }
}