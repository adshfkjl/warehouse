using System;
using System.Security.Cryptography;
using System.Text;

namespace PLCManagement.API.Utilities
{
    public static class PasswordHelper
    {
        public static (string Hash, string Salt) CreatePasswordHash(string password)
        {
            // 生成随机盐值
            var saltBytes = new byte[64];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(saltBytes);
            }
            var salt = Convert.ToBase64String(saltBytes);

            // 使用PBKDF2算法生成哈希
            using var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, 10000, HashAlgorithmName.SHA512);
            var hashBytes = pbkdf2.GetBytes(64); // 64字节的哈希值
            var hash = Convert.ToBase64String(hashBytes);

            return (hash, salt);
        }

        public static bool VerifyPassword(string password, string storedHash, string storedSalt)
        {
            var saltBytes = Convert.FromBase64String(storedSalt);
            var storedHashBytes = Convert.FromBase64String(storedHash);

            // 使用相同的参数重新计算哈希
            using var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, 10000, HashAlgorithmName.SHA512);
            var computedHashBytes = pbkdf2.GetBytes(64);

            // 比较哈希值
            return CryptographicOperations.FixedTimeEquals(computedHashBytes, storedHashBytes);
        }
    }
}