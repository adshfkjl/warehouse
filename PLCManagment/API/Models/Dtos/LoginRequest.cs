using System.ComponentModel.DataAnnotations;

namespace PLCManagement.API.Models.Dtos
{
    public class LoginRequest
    {
        [Required]
        public string Username { get; set; }

        [Required]
        public string Password { get; set; }

        [Required]
        public string DeviceId { get; set; }
    }

    public class RefreshTokenRequest
    {
        [Required]
        public string Token { get; set; }

        [Required]
        public string RefreshToken { get; set; }
    }


    public class CreateUserDto
    {
        [Required]
        [StringLength(50)]
        public required string Username { get; set; }

        [Required]
        [StringLength(100)]
        public string? Password { get; set; }

        [StringLength(100)]
        public string? FullName { get; set; }

        [StringLength(20)]
        public string? EmployeeId { get; set; }

        public bool IsActive { get; set; } = true;
    }

    public class UpdateUserDto
    {
        [StringLength(100)]
        public string FullName { get; set; }

        [StringLength(20)]
        public string EmployeeId { get; set; }

        public bool? IsActive { get; set; }
    }

    public class ChangePasswordDto
    {
        [Required]
        public string CurrentPassword { get; set; }

        [Required]
        public string NewPassword { get; set; }
    }

    public class AssignRolesDto
    {
        public List<int> RoleIds { get; set; } = new List<int>();
    }

    public class UserQueryParameters
    {
        public string Username { get; set; }
        public string FullName { get; set; }
        public string EmployeeId { get; set; }
        public bool? IsActive { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }


    public class AssignPermissionsDto
    {
        public List<int> PermissionIds { get; set; } = new List<int>();
    }


}