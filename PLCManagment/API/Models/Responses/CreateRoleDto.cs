using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PLCManagement.API.Models.Dtos
{
    public class CreateRoleDto
    {
        [Required]
        [StringLength(50)]
        public string Name { get; set; }

        [StringLength(255)]
        public string? Description { get; set; }
    }

    public class UpdateRoleDto
    {
        [StringLength(255)]
        public string? Description { get; set; }
    }

    public class LoginResponse
    {
        public string Token { get; set; }
        public string RefreshToken { get; set; }
        public int UserId { get; set; }
        public string FullName { get; set; }
        public List<string> Roles { get; set; }
        public List<string> Permissions { get; set; }
        public int ExpiresIn { get; set; }
    }

    public class AuthResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Token { get; set; }
        public string RefreshToken { get; set; }
        public int UserId { get; set; }
        public string FullName { get; set; }
        public List<string> Roles { get; set; }
        public List<string> Permissions { get; set; }
        public int ExpiresIn { get; set; }
    }

    public class UserResponse
    {
        public int Id { get; set; }
        public string Username { get; set; }
        public string FullName { get; set; }
        public string EmployeeId { get; set; }
        public bool IsActive { get; set; }
        public DateTime? LastLoginTime { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<string> Roles { get; set; }
    }

    public class UserDetailResponse : UserResponse
    {
        public List<string> Permissions { get; set; }
    }

    public class UserCreateResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public UserResponse User { get; set; }
    }
    public class RoleResponse
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class RoleDetailResponse : RoleResponse
    {
        public List<string> Permissions { get; set; }
    }

}