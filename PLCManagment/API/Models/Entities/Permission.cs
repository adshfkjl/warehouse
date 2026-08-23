using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PLCManagement.API.Models.Entities
{
    public class Permission
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; }

        [StringLength(255)]
        public string? Description { get; set; }

        [Required]
        [StringLength(50)]
        public required string Code { get; set; }

        // 导航属性
        public virtual ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
    }
}