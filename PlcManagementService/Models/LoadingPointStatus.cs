namespace PLCService.Models
{
    public class LoadingPointStatus
    {
        public string PLCID { get; set; }
        public int PLCLocationCode { get; set; }
        public string CurrentPalletNumber { get; set; }
        public decimal Weight { get; set; }
    }
}
