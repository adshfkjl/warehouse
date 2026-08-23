// PLCManagement.API/Services/LogService.cs
using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PLCManagement.API.Data;
using PLCManagement.API.Interfaces;
using PLCManagement.API.Models;

namespace PLCManagement.API.Services
{
    public class LogService : ILogService
    {
        private readonly ApplicationDbContext _context;

        public LogService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task LogInformation(string message)
        {
            await LogAsync("Information", message);
        }

        public async Task LogWarning(string message)
        {
            await LogAsync("Warning", message);
        }

        public async Task LogError(string message)
        {
            await LogAsync("Error", message);
        }

        public async Task LogDebug(string message)
        {
            await LogAsync("Debug", message);
        }

        private async Task LogAsync(string level, string message)
        {
            try
            {
                var logEntry = new LogEntry
                {
                    Date = DateTime.Now,
                    Level = level,
                    Message = message,
                    Logger = "PLCManagement.API"
                };

                _context.LogEntrys.Add(logEntry);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // 如果数据库日志记录失败，至少记录到控制台
                Console.WriteLine($"Failed to log to database: {ex.Message}");
                Console.WriteLine($"Original log message ({level}): {message}");
            }
        }
    }
}