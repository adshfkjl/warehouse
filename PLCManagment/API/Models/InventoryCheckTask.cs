using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PLCManagement.API.Models
{
    public class InventoryCheckTask
    {
        [Key]
        public long Id { get; set; }

        public string TaskNo { get; set; } = string.Empty;
        public string PLCID { get; set; } = string.Empty;
        public string TrayStart { get; set; } = string.Empty;
        public string TrayEnd { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int TotalCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string Message { get; set; } = string.Empty;
        public ICollection<InventoryCheckItem> Items { get; set; } = new List<InventoryCheckItem>();
    }
}
