namespace PLCService.Models
{
    public static class ModbusAddress
    {
        // 上位机向PLC发送数据地址
        public const ushort WorkMode = 22000;          // D22000
        public const ushort ShelfSelection = 22001;    // D22001
        // 添加其他地址...
        public const ushort Heartbeat = 22027;         // D22027

        // PLC向上位机发送数据地址
        public const ushort PlcStatus = 23000;         // D23000
        public const ushort WorkStatus = 23001;        // D23001
        public const ushort TaskResponse = 23002;      // D23002
        public const ushort ForksStatus = 23003;       // D23003
        public const ushort ShelfDetection = 23004;    // D23004
        public const ushort PositionStatus = 23005;    // D23005
        public const ushort ResetCompleted = 23010;    // D23010
        public const ushort InboundCompleted = 23012;  // D23012
        public const ushort OutboundCompleted = 23013; // D23013
        public const ushort RelocationCompleted = 23014; // D23014
        public const ushort AlarmInfo = 23019;         // D23019
        public const ushort PosX = 23030;              // D23030
        public const ushort PosY = 23032;              // D23032
        public const ushort PosZ = 23034;              // D23034
        public const ushort SpeedX = 23036;            // D23036
        public const ushort SpeedY = 23038;            // D23038
        public const ushort BoxWeight1 = 23042;        // D23042
        public const ushort BoxWeight2 = 23044;        // D23044
    }
}