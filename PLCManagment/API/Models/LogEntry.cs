
// PLCManagement.API/Models/LogEntry.cs
using System;
using System.ComponentModel.DataAnnotations;

namespace PLCManagement.API.Models
{
    public class LogEntry
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public DateTime Date { get; set; }

        [StringLength(255)]
        public string Thread { get; set; }

        [StringLength(50)]
        public string Level { get; set; }

        [StringLength(255)]
        public string Logger { get; set; }

        public string Message { get; set; }

        public string Exception { get; set; }
    }
}

