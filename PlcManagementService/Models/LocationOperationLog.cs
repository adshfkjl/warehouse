using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PlcManagementService.Models
{
    public class LocationOperationLog
    {
        public long ID { get; set; }
        public DateTime CreateTime { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string PLCID { get; set; }
        public int? OutboundShelf { get; set; }
        public int? OutboundPosition { get; set; }
        public int? InboundShelf { get; set; }
        public int? InboundPosition { get; set; }
        public int LoadingPoint { get; set; }
        public int OperationType { get; set; }
        public int OperationResult { get; set; }
        public decimal? WeightBegin { get; set; }
        public decimal? WeightEnd { get; set; }
        public decimal? IncreaseQuantity { get; set; }
        public decimal? DecreaseQuantity { get; set; }
    }
}
