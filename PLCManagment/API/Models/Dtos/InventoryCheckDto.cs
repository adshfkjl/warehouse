using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PLCManagement.API.Models.Dtos
{
    public static class InventoryCheckStatuses
    {
        public const string Pending = "Pending";
        public const string Running = "Running";
        public const string Completed = "Completed";
        public const string CompletedWithErrors = "CompletedWithErrors";
        public const string Failed = "Failed";
        public const string Canceled = "Canceled";
        public const string Succeeded = "Succeeded";
        public const string Skipped = "Skipped";
        public const string NotReady = "NotReady";
        public const string Ready = "Ready";
        public const string Scheduled = "Scheduled";
    }

    public class InventoryCheckOutboundRangeRequest
    {
        [Required]
        public string PlcId { get; set; } = string.Empty;

        [Required]
        public string TrayStart { get; set; } = string.Empty;

        [Required]
        public string TrayEnd { get; set; } = string.Empty;
    }

    public class InventoryCheckTaskResponse
    {
        public long TaskId { get; set; }
        public string TaskNo { get; set; } = string.Empty;
        public string PLCID { get; set; } = string.Empty;
        public string TrayStart { get; set; } = string.Empty;
        public string TrayEnd { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int TotalCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<InventoryCheckItemResponse> Items { get; set; } = new List<InventoryCheckItemResponse>();
    }

    public class InventoryCheckItemResponse
    {
        public long Id { get; set; }
        public string PLCID { get; set; } = string.Empty;
        public string Tray { get; set; } = string.Empty;
        public int Shelf { get; set; }
        public int Position { get; set; }
        public int? OutboundLoadingPoint { get; set; }
        public string OutboundStatus { get; set; } = string.Empty;
        public string OutboundMessage { get; set; } = string.Empty;
        public string InboundStatus { get; set; } = string.Empty;
        public int? InboundLoadingPoint { get; set; }
        public string InboundMessage { get; set; } = string.Empty;
    }
}
