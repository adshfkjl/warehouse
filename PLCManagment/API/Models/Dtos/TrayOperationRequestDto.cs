namespace PLCManagement.API.Models.Dtos
{
    public class TrayOperationRequestDto
    {
        public required string PalletCode { get; set; }
        public int LoadingPoint { get; set; }

        public string? BillID { get; set; }
        public string? BillNO { get; set; }
        public int ITM { get; set; }

        public double QTY { get; set; }
        public string? REM { get; set; }
    }

    public class TrayTransferRequestDto
    {
        public required string PalletCode { get; set; }
        public int LoadingPoint { get; set; }
        public required string TargetPalletCode { get; set; } // 用于移库操作的目标托盘
    }
}