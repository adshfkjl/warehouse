using Microsoft.EntityFrameworkCore;
using PLCManagement.API.Models.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace PLCManagement.API.Data
{
    public static class SeedData
    {
        public static async Task Initialize(IServiceProvider serviceProvider)
        {
            using var context = new ApplicationDbContext(
                serviceProvider.GetRequiredService<DbContextOptions<ApplicationDbContext>>());

            // 确保数据库已创建
            await context.Database.EnsureCreatedAsync();

            // 如果没有任何权限，则添加基本权限
            if (!context.Permissions.Any())
            {
                var permissions = new List<Permission>
                {
                    new Permission { Name = "查看用户", Code = "users:read" },
                    new Permission { Name = "创建用户", Code = "users:create" },
                    new Permission { Name = "更新用户", Code = "users:update" },
                    new Permission { Name = "删除用户", Code = "users:delete" },
                    new Permission { Name = "管理用户角色", Code = "users:manage-roles" },

                    new Permission { Name = "查看角色", Code = "roles:read" },
                    new Permission { Name = "创建角色", Code = "roles:create" },
                    new Permission { Name = "更新角色", Code = "roles:update" },
                    new Permission { Name = "删除角色", Code = "roles:delete" },
                    new Permission { Name = "管理角色权限", Code = "roles:manage-permissions" },

                    new Permission { Name = "查看库存", Code = "inventory:read" },
                    new Permission { Name = "收货操作", Code = "inventory:receive" },
                    new Permission { Name = "拣货操作", Code = "inventory:pick" },
                    new Permission { Name = "上架操作", Code = "inventory:putaway" },
                    new Permission { Name = "移库操作", Code = "inventory:transfer" },
                    new Permission { Name = "库存调整", Code = "inventory:adjust" },

                    new Permission { Name = "PDA基本操作", Code = "pda:basic-operation" },
                    new Permission { Name = "PDA管理员操作", Code = "pda:admin-operation" }
                };

                await context.Permissions.AddRangeAsync(permissions);
                await context.SaveChangesAsync();
            }

            // 如果没有任何角色，则添加基本角色
            if (!context.Roles.Any())
            {
                var adminRole = new Role
                {
                    Name = "Administrator",
                    Description = "系统管理员"
                };

                var operatorRole = new Role
                {
                    Name = "Operator",
                    Description = "仓库操作员"
                };

                var pdaUserRole = new Role
                {
                    Name = "PDAUser",
                    Description = "PDA用户"
                };

                await context.Roles.AddRangeAsync(adminRole, operatorRole, pdaUserRole);
                await context.SaveChangesAsync();

                // 为管理员角色分配所有权限
                var allPermissions = await context.Permissions.ToListAsync();
                var adminRolePermissions = allPermissions.Select(p => new RolePermission
                {
                    RoleId = adminRole.Id,
                    PermissionId = p.Id
                });

                // 为操作员角色分配基本权限
                var operatorPermissions = allPermissions
                    .Where(p => p.Code.StartsWith("inventory:") || p.Code == "pda:basic-operation")
                    .Select(p => new RolePermission
                    {
                        RoleId = operatorRole.Id,
                        PermissionId = p.Id
                    });

                // 为PDA用户分配PDA权限
                var pdaUserPermissions = allPermissions
                    .Where(p => p.Code == "pda:basic-operation" || p.Code.StartsWith("inventory:"))
                    .Select(p => new RolePermission
                    {
                        RoleId = pdaUserRole.Id,
                        PermissionId = p.Id
                    });

                await context.RolePermissions.AddRangeAsync(adminRolePermissions);
                await context.RolePermissions.AddRangeAsync(operatorPermissions);
                await context.RolePermissions.AddRangeAsync(pdaUserPermissions);
                await context.SaveChangesAsync();
            }

            // 如果没有管理员用户，则添加一个
            if (!context.Users.Any(u => u.Username == "admin"))
            {
                var adminUser = new User
                {
                    Username = "admin",
                    FullName = "系统管理员",
                    IsActive = true
                };

                // 设置密码 (实际应用中应该使用更安全的哈希方式)
                var salt = Guid.NewGuid().ToString();
                adminUser.Salt = salt;
                adminUser.PasswordHash = HashPassword("admin123", salt);

                await context.Users.AddAsync(adminUser);
                await context.SaveChangesAsync();

                // 为管理员用户分配管理员角色
                var adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == "Administrator");
                if (adminRole != null)
                {
                    await context.UserRoles.AddAsync(new UserRole
                    {
                        UserId = adminUser.Id,
                        RoleId = adminRole.Id
                    });
                    await context.SaveChangesAsync();
                }
            }
        }

        private static string HashPassword(string password, string salt)
        {
            // 实际应用中应该使用更安全的哈希算法
            using var hmac = new System.Security.Cryptography.HMACSHA512(System.Text.Encoding.UTF8.GetBytes(salt));
            var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(password));
            return Convert.ToBase64String(hash);
        }
    }
}