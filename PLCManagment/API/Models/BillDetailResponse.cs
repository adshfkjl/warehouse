namespace PLCManagement.API.Models
{
    public class BillDetailResponse
    {
        public string? BillID { get; set; }
        public string? BillNO { get; set; }
        public int ITM { get; set; }
        public string? MaterialNo { get; set; }
        public string? MaterialName { get; set; }
        public string? Spc { get; set; }
        public string? WareHouseNo { get; set; }
        public string? BarNo { get; set; }
        public decimal Price { get; set; }
        public decimal Qty { get; set; }
        public decimal Total { get; set; }
        public string? Remark { get; set; }
        public string? PalletCode { get; set; }
        public decimal? Quantity { get; set; }
        public string? Flag { get; set; }
        public long SortID { get; set; }
        public int ShelfStatus { get; set; }
        public decimal Weight { get; set; }
    }
}