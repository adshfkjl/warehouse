using System;
using System.IO;
using NLog;

namespace PLCService.Services
{
    public static class LogService
    {
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        private static LogLevel _currentLogLevel = LogLevel.Info; // 默认级别

        public enum LogLevel
        {
            Debug = 0,
            Info = 1,
            Warning = 2,
            Error = 3
        }

        static LogService()
        {
            if (!Directory.Exists(LogPath))
                Directory.CreateDirectory(LogPath);
        }

        public static void SetLogLevel(LogLevel level)
        {
            _currentLogLevel = level;
        }

        public static void Debug(string message)
        {
            if (_currentLogLevel <= LogLevel.Debug)
                WriteLog("DEBUG", message);
        }

        public static void Info(string message)
        {
            if (_currentLogLevel <= LogLevel.Info)
                WriteLog("INFO", message);
        }

        public static void Error(string message)
        {
            if (_currentLogLevel <= LogLevel.Error)
                WriteLog("ERROR", message);
        }

        public static void Warning(string message)
        {
            if (_currentLogLevel <= LogLevel.Warning)
                WriteLog("WARNING", message);
        }

        public static void Verbose(string message)
        {
            if (_currentLogLevel <= LogLevel.Debug)
                WriteLog("VERBOSE", message);
        }

        private static void WriteLog(string level, string message)
        {
            try
            {
                var logFile = Path.Combine(LogPath, $"plc_service_{DateTime.Now:yyyyMMdd}.log");
                var logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";

                File.AppendAllText(logFile, logMessage);
            }
            catch
            {
                ;// 防止日志记录本身出错导致服务崩溃
            }
        }
    }
}