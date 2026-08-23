using System;
using System.ComponentModel.DataAnnotations;

namespace PLCManagement.API.Models
{
    public class InventoryCheckItem
    {
        [Key]
        public long Id { get; set; }

        public long TaskId { get; set; }
        public InventoryCheckTask? Task { get; set; }
        public string PLCID { get; set; } = string.Empty;
        public string Tray { get; set; } = string.Empty;
        public int Shelf { get; set; }
        public int Position { get; set; }
        public int OriginalShelfStatus { get; set; }
        public int? OutboundLoadingPoint { get; set; }
        public string OutboundStatus { get; set; } = string.Empty;
        public long? OutboundOperationLogId { get; set; }
        public DateTime? OutboundStartedAt { get; set; }
        public DateTime? OutboundCompletedAt { get; set; }
        public string OutboundMessage { get; set; } = string.Empty;
        public string InboundStatus { get; set; } = string.Empty;
        public int? InboundLoadingPoint { get; set; }
        public DateTime? InboundScheduledAt { get; set; }
        public string InboundMessage { get; set; } = string.Empty;
    }
}
