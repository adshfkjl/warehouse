using System;

namespace PLCService.Models
{
    public class PlcConfiguration
    {
        public int Id { get; set; }
        public string PlcId { get; set; }
        public string IpAddress { get; set; }
        public int Port { get; set; }
        public byte SlaveId { get; set; }
        public string Description { get; set; }
        public bool IsActive { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? LastTestTime { get; set; }
        public string LastConnectionStatus { get; set; }
        public string LastErrorMessage { get; set; }

        // 其他字段根据数据库表结构添加
        public int MainSpeed { get; set; }
        public int AuxSpeed { get; set; }
        public decimal ForkLength { get; set; }
        public int Row { get; set; }
        public int Lev { get; set; }
        public bool IsResetCompleted { get; set; }
        public bool IsInboundCompleted { get; set; }
        public bool IsOutboundCompleted { get; set; }
        public bool IsRelocationCompleted { get; set; }
        public bool IsEmergencyStop { get; set; }
        public decimal BoxWeightA { get; set; }
        public decimal BoxWeightB { get; set; }
        public DateTime? TaskCreateTime { get; set; }
        public DateTime? TaskStartTime { get; set; }
        public DateTime? TaskEndTime { get; set; }
        public int TaskStat { get; set; }
        public int OutboundShelf { get; set; }
        public int OutboundPosition { get; set; }
        public int InboundShelf { get; set; }
        public int InboundPosition { get; set; }
        public int LoadingPoint { get; set; }
        public long? OperationID { get; set; }
        public int OperationType { get; set; }
        public int OperationResult { get; set; }
        public string PalletCodeA { get; set; }
        public string PalletCodeB { get; set; }
        public bool ForksStat { get; set; }
        public bool ServoStat { get; set; }
        public string ForksPalletCode { get; set; }
        public decimal PosX { get; set; }
        public decimal PosY { get; set; }
        public decimal PosZ { get; set; }


        public int RegisterAddrOffset {  get; set; }
        public bool AutoRun { get; set; }

    }
}