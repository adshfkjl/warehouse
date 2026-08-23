// PLCManagement.API/Models/PlcConfiguration.cs
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PLCManagement.API.Models
{
    public class PlcConfiguration
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public required string PlcId { get; set; }

        [Required]
        [StringLength(15)]
        public required string IpAddress { get; set; }

        [Column("IpAddress_PC")]
        [StringLength(45)]
        public string? IpAddressPC { get; set; }

        [Required]
        public int Port { get; set; }

        [Required]
        public byte SlaveId { get; set; }

        public long? OperationID { get; set; }
        public string? Description { get; set; }

        [Required]
        public bool IsActive { get; set; }

        public DateTime? UpdatedAt { get; set; }
        public DateTime? LastTestTime { get; set; }
        public string? LastConnectionStatus { get; set; }
        public string? LastErrorMessage { get; set; }

        public int? MainSpeed { get; set; }
        public int? AuxSpeed { get; set; }

        [Column(TypeName = "numeric(18, 3)")]
        public decimal? ForkLength { get; set; }

        [Required]
        [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
        public int Row { get; set; } = 32;

        [Required]
        [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
        public int Lev { get; set; } = 7;

        public bool? IsResetCompleted { get; set; }
        public bool? IsInboundCompleted { get; set; }
        public bool? IsOutboundCompleted { get; set; }
        public bool? IsRelocationCompleted { get; set; }
        public bool? IsEmergencyStop { get; set; }

        [Column(TypeName = "decimal(10, 2)")]
        public decimal? BoxWeightA { get; set; }

        [Column(TypeName = "decimal(10, 2)")]
        public decimal? BoxWeightB { get; set; }

        [Column(TypeName = "decimal(10, 3)")]
        public decimal? PosX { get; set; }

        [Column(TypeName = "decimal(10, 3)")]
        public decimal? PosY { get; set; }

        [Column(TypeName = "decimal(10, 3)")]
        public decimal? PosZ { get; set; }

        public DateTime? TaskCreateTime { get; set; }
        public DateTime? TaskStartTime { get; set; }
        public DateTime? TaskEndTime { get; set; }

        public int? OutboundShelf { get; set; }
        public int? OutboundPosition { get; set; }
        public int? InboundShelf { get; set; }
        public int? InboundPosition { get; set; }
        public int? LoadingPoint { get; set; }

        public int? OperationType { get; set; }
        public int? OperationResult { get; set; }

        [StringLength(50)]
        public string? PalletCodeA { get; set; }

        [StringLength(50)]
        public string? PalletCodeB { get; set; }

        public bool? ForksStat { get; set; }

        public bool? ServoStat { get; set; }

        [StringLength(50)]
        public string? ForksPalletCode { get; set; }
        public int RegisterAddrOffset {  get; set; }

    }
}


/*
// PLCManagement.API/Models/PlcConfiguration.cs
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PLCManagement.API.Models
{
    public class PlcConfiguration
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public required string PlcId { get; set; }

        [Required]
        [StringLength(15)]
        public required string IpAddress { get; set; }

        [Required]
        public int Port { get; set; }

        [Required]
        public byte SlaveId { get; set; }

        public string? Description { get; set; }

        [Required]
        public bool IsActive { get; set; }

        public DateTime? UpdatedAt { get; set; }

        public DateTime? LastTestTime { get; set; }

        public string? LastConnectionStatus { get; set; }

        public string? LastErrorMessage { get; set; }

        public int MainSpeed { get; set; }

        public int AuxSpeed { get; set; }
        public decimal ForkLength { get; set; }
    }
}
*/
