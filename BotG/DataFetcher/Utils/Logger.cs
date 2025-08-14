using System;
using System.IO;

namespace DataFetcher.Utils
{
    public static class Logger
    {
        private static string _logFile = "datafetcher.log";
        public enum LogLevel { Info, Warning, Error }

        /// <summary>
        /// Ghi log ra file và console.
        /// </summary>
        public static void Log(string message, LogLevel level = LogLevel.Info)
        {
            var logMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
            Console.WriteLine(logMsg);
            File.AppendAllText(_logFile, logMsg + Environment.NewLine);
        }

        /// <summary>
        /// Đặt file log.
        /// </summary>
        public static void SetLogFile(string path)
        {
            _logFile = path;
        }
    }
}
