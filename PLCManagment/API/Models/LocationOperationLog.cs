
// PLCManagement.API/Models/LocationOperationLog.cs
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PLCManagement.API.Models
{
    public class LocationOperationLog
    {
        [Key]
        public long Id { get; set; }

        public required DateTime CreateTime { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public required string PLCID { get; set; }
        public int OutboundShelf { get; set; }
        public int OutboundPosition { get; set; }
        public int InboundShelf { get; set; }
        public int InboundPosition { get; set; }
        public int LoadingPoint { get; set; }
        public int OperationType { get; set; }
        public int OperationResult { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? WeightBegin { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? WeightEnd { get; set; }


    }
}

