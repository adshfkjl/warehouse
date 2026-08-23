using PLCManagement.API.Models.Entities;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PLCManagement.API.Models.Entities
{
    public class RefreshToken
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }

        [Required]
        [StringLength(100)]
        public string Token { get; set; }

        [Required]
        public DateTime Expires { get; set; }

        [Required]
        public DateTime Created { get; set; }

        [StringLength(50)]
        public string CreatedByIp { get; set; }

        [StringLength(50)]
        public string DeviceId { get; set; }

        public DateTime? Revoked { get; set; }

        [StringLength(50)]
        public string RevokedByIp { get; set; }

        [StringLength(255)]
        public string ReplacementToken { get; set; }

        [ForeignKey("UserId")]
        public virtual User User { get; set; }

        public bool IsActive => Revoked == null && !IsExpired;
        public bool IsExpired => DateTime.UtcNow >= Expires;
    }
}