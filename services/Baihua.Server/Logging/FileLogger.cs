using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Baihua.Server.Logging
{
    public class FileLogger : ILogger
    {
        private readonly string _filePath;
        private readonly string _categoryName;
        private readonly object _lock = new();

        public FileLogger(string filePath, string categoryName)
        {
            _filePath = filePath;
            _categoryName = categoryName;
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch { }
        }

        // Implement explicit interface to avoid nullable constraint mismatch warnings
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter == null) return;
            try
            {
                var message = formatter(state, exception);
                var logLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_categoryName}: {message}";
                if (exception != null)
                {
                    logLine += "\n" + exception;
                }
                lock (_lock)
                {
                    File.AppendAllText(_filePath, logLine + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    public class FileLoggerProvider : ILoggerProvider
    {
        private readonly string _filePath;

        public FileLoggerProvider(string filePath)
        {
            _filePath = filePath;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new FileLogger(_filePath, categoryName);
        }

        public void Dispose() { }
    }
}
