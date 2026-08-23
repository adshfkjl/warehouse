// PLCManagement.Core/Models/ModbusResponse.cs
namespace PLCManagement.Core.Models
{
    public class ModbusResponse<T>
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public T Data { get; set; }
    }

    public class ModbusResponse
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
    }
}