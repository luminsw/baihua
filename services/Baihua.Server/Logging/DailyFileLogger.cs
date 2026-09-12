using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Baihua.Server.Logging;

/// <summary>
/// 按日期轮转的日志记录器
/// </summary>
public class DailyFileLogger : ILogger, IDisposable
{
    private readonly string _basePath;
    private readonly string _fileNamePrefix;
    private readonly string _categoryName;
    private readonly object _lock = new();

    private string _currentFilePath = "";
    private DateTime _currentDate = DateTime.MinValue;
    private StreamWriter? _writer;
    private bool _disposed;
    private readonly LogLevel _minimumLevel;

    // 保留天数
    private readonly int _retentionDays;

    public DailyFileLogger(string basePath, string fileNamePrefix, string categoryName, int retentionDays = 7,
        LogLevel minimumLevel = LogLevel.Information)
    {
        _basePath = basePath;
        _fileNamePrefix = fileNamePrefix;
        _categoryName = categoryName;
        _retentionDays = retentionDays;
        _minimumLevel = minimumLevel;

        // 确保日志目录存在
        EnsureDirectoryExists();

        // 初始化当前日志文件
        OpenLogFile();
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return new ScopeDisposable();
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minimumLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || formatter == null)
            return;

        // 检查是否需要切换日志文件（日期变化）
        EnsureLogFileForToday();

        // 获取日志消息
        var message = formatter(state, exception);

        // 尝试获取结构化数据
        var structuredData = GetStructuredData(state);

        // 构建日志行（包含更多上下文信息）
        var logLine = FormatLogLine(logLevel, eventId, message, exception, structuredData);

        lock (_lock)
        {
            try
            {
                _writer?.WriteLine(logLine);
                _writer?.Flush();
            }
            catch (Exception)
            {
                // 写入失败时静默处理，避免递归日志
            }
        }
    }

    private string? GetStructuredData<TState>(TState state)
    {
        if (state is IReadOnlyList<KeyValuePair<string, object>> keyValuePairs)
        {
            var data = new Dictionary<string, object>();
            foreach (var pair in keyValuePairs)
            {
                if (pair.Key != "{OriginalFormat}")
                {
                    data[pair.Key] = pair.Value;
                }
            }
            if (data.Count > 0)
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Serialize(data);
                }
                catch { }
            }
        }
        return null;
    }

    private void EnsureLogFileForToday()
    {
        var today = DateTime.Now.Date;

        if (_currentDate != today)
        {
            lock (_lock)
            {
                if (_currentDate != today)
                {
                    // 切换日志文件
                    CloseLogFile();
                    OpenLogFile();

                    // 清理旧日志
                    CleanupOldLogs();
                }
            }
        }
    }

    private void OpenLogFile()
    {
        _currentDate = DateTime.Now.Date;
        _currentFilePath = Path.Combine(_basePath, $"{_fileNamePrefix}-{_currentDate:yyyyMMdd}.log");

        try
        {
            _writer = new StreamWriter(_currentFilePath, append: true, encoding: System.Text.Encoding.UTF8)
            {
                AutoFlush = true
            };
        }
        catch (Exception)
        {
            // 打开失败时静默处理
            _writer = null;
        }
    }

    private void CloseLogFile()
    {
        try
        {
            _writer?.Close();
            _writer?.Dispose();
        }
        catch { }
        finally
        {
            _writer = null;
        }
    }

    private void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_basePath))
            {
                Directory.CreateDirectory(_basePath);
            }
        }
        catch { }
    }

    private void CleanupOldLogs()
    {
        try
        {
            var cutoffDate = DateTime.Now.Date.AddDays(-_retentionDays);
            var logFiles = Directory.GetFiles(_basePath, $"{_fileNamePrefix}-*.log");

            foreach (var file in logFiles)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    // 根据文件名日期或文件修改时间判断
                    if (fileInfo.LastWriteTime.Date < cutoffDate)
                    {
                        fileInfo.Delete();
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private string FormatLogLine(LogLevel logLevel, EventId eventId, string message, Exception? exception, string? structuredData = null)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var level = GetLogLevelShortName(logLevel);

        // 基础格式：时间 [级别] 类别: 消息
        var sb = new System.Text.StringBuilder();
        sb.Append($"{timestamp} [{level}] {_categoryName}: {message}");

        // 如果有 EventId，添加（仅非0时）
        if (eventId.Id != 0)
        {
            sb.Append($" [EventId:{eventId.Id}]");
        }

        // 如果有结构化数据，添加
        if (!string.IsNullOrEmpty(structuredData))
        {
            sb.Append($" [Data:{structuredData}]");
        }

        // 如果有异常，添加异常信息
        if (exception != null)
        {
            sb.AppendLine();
            sb.Append(exception.ToString());
        }

        return sb.ToString();
    }

    private static string GetLogLevelShortName(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => "TRCE",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRIT",
            _ => "UNKN"
        };
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_lock)
        {
            CloseLogFile();
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private class ScopeDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// 按日期轮转的文件日志提供程序
/// 内置类别级别过滤，确保第三方框架的DBG日志不会淹没应用日志
/// </summary>
public class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly string _basePath;
    private readonly string _fileNamePrefix;
    private readonly int _retentionDays;
    private readonly LogLevel _globalMinimumLevel;
    private readonly ConcurrentDictionary<string, DailyFileLogger> _loggers = new();
    private bool _disposed;

    /// <summary>
    /// 类别级别过滤规则：类别前缀 -> 最低级别
    /// 匹配时使用最长前缀匹配
    /// </summary>
    private readonly List<(string Prefix, LogLevel Level)> _categoryFilters;

    public DailyFileLoggerProvider(string basePath, string fileNamePrefix, int retentionDays = 7,
        LogLevel globalMinimumLevel = LogLevel.Information,
        Dictionary<string, LogLevel>? categoryFilters = null)
    {
        _basePath = basePath;
        _fileNamePrefix = fileNamePrefix;
        _retentionDays = retentionDays;
        _globalMinimumLevel = globalMinimumLevel;

        // 构建类别过滤规则列表（按前缀长度降序排列，确保最长匹配优先）
        _categoryFilters = new List<(string Prefix, LogLevel Level)>();
        if (categoryFilters != null)
        {
            foreach (var kv in categoryFilters)
            {
                _categoryFilters.Add((kv.Key, kv.Value));
            }
            _categoryFilters.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DailyFileLoggerProvider));

        var minLevel = GetMinimumLevelForCategory(categoryName);

        return _loggers.GetOrAdd(categoryName,
            name => new DailyFileLogger(_basePath, _fileNamePrefix, name, _retentionDays, minLevel));
    }

    /// <summary>
    /// 根据类别名获取最低日志级别（最长前缀匹配）
    /// </summary>
    private LogLevel GetMinimumLevelForCategory(string categoryName)
    {
        foreach (var (prefix, level) in _categoryFilters)
        {
            if (categoryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return level;
            }
        }
        return _globalMinimumLevel;
    }

    public void Dispose()
    {
        if (_disposed) return;

        foreach (var logger in _loggers.Values)
        {
            logger.Dispose();
        }
        _loggers.Clear();

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}