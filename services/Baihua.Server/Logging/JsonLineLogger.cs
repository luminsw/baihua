using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Baihua.Server.Logging;

/// <summary>
/// 结构化JSON Lines日志记录器。
/// 每行一条JSON记录，可被jq/grep/任何日志工具高效查询。
/// 所有类别共享同一个Writer，避免多Writer写同一文件的冲突问题。
/// </summary>
public class JsonLineLogger : ILogger
{
    private readonly string _categoryName;
    private readonly LogLevel _minimumLevel;
    private readonly JsonLineLoggerWriter _writer;

    public JsonLineLogger(string categoryName, LogLevel minimumLevel, JsonLineLoggerWriter writer)
    {
        _categoryName = categoryName;
        _minimumLevel = minimumLevel;
        _writer = writer;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minimumLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || formatter == null) return;

        var message = formatter(state, exception);

        // 构建JSON行
        var entry = new LogEntry
        {
            Ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            Level = GetLevelShort(logLevel),
            Cat = _categoryName,
            Msg = message,
        };

        if (eventId.Id != 0)
        {
            entry.Eid = eventId.Id;
            entry.Ename = eventId.Name;
        }

        // 提取结构化参数
        if (state is IReadOnlyList<KeyValuePair<string, object>> keyValuePairs)
        {
            var props = new Dictionary<string, object>();
            foreach (var pair in keyValuePairs)
            {
                if (pair.Key != "{OriginalFormat}")
                {
                    props[pair.Key] = pair.Value;
                }
            }
            if (props.Count > 0)
            {
                entry.Props = props;
            }
        }

        if (exception != null)
        {
            entry.Err = exception.GetType().Name;
            entry.ErrMsg = exception.Message;
            entry.Stack = exception.StackTrace;
        }

        _writer.WriteLine(entry);
    }

    private static string GetLevelShort(LogLevel level) => level switch
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

/// <summary>
/// 共享的日志写入器。所有JsonLineLogger实例共享同一个Writer，
/// 通过ConcurrentQueue实现异步写入，避免阻塞调用线程。
/// </summary>
public class JsonLineLoggerWriter : IDisposable
{
    private readonly string _basePath;
    private readonly string _fileNamePrefix;
    private readonly int _retentionDays;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writeTask;
    private readonly int _flushIntervalMs;

    private string _currentFilePath = "";
    private DateTime _currentDate = DateTime.MinValue;
    private StreamWriter? _writer;
    private readonly object _fileLock = new();
    private bool _disposed;
    private long _linesWritten;

    public JsonLineLoggerWriter(string basePath, string fileNamePrefix, int retentionDays = 7, int flushIntervalMs = 200)
    {
        _basePath = basePath;
        _fileNamePrefix = fileNamePrefix;
        _retentionDays = retentionDays;
        _flushIntervalMs = flushIntervalMs;

        try
        {
            if (!Directory.Exists(_basePath))
                Directory.CreateDirectory(_basePath);
        }
        catch { }

        OpenLogFile();

        // 后台写入任务：从队列取日志行，批量写入文件
        _writeTask = Task.Run(WriteLoopAsync);
    }

    public long LinesWritten => Interlocked.Read(ref _linesWritten);

    public void WriteLine(LogEntry entry)
    {
        try
        {
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            _queue.Enqueue(json);
        }
        catch { }
    }

    private async Task WriteLoopAsync()
    {
        var buffer = new List<string>(256);

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // 等待有数据或超时
                await Task.Delay(_flushIntervalMs, _cts.Token);

                // 批量取出队列中的所有行
                while (_queue.TryDequeue(out var line))
                {
                    buffer.Add(line);
                }

                if (buffer.Count == 0) continue;

                // 写入文件
                lock (_fileLock)
                {
                    EnsureLogFileForToday();
                    if (_writer == null) { buffer.Clear(); continue; }

                    foreach (var line in buffer)
                    {
                        _writer.WriteLine(line);
                    }
                    _writer.Flush();
                    Interlocked.Add(ref _linesWritten, buffer.Count);
                }

                buffer.Clear();
            }
            catch (OperationCanceledException) { break; }
            catch { buffer.Clear(); }
        }

        // 刷出剩余
        FlushRemaining();
    }

    private void EnsureLogFileForToday()
    {
        var today = DateTime.Now.Date;
        if (_currentDate != today)
        {
            lock (_fileLock)
            {
                if (_currentDate != today)
                {
                    CloseLogFile();
                    OpenLogFile();
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
            _writer = new StreamWriter(_currentFilePath, append: true, Encoding.UTF8)
            {
                AutoFlush = false // 手动Flush，批量写入后统一刷盘
            };
        }
        catch
        {
            _writer = null;
        }
    }

    private void CloseLogFile()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch { }
        _writer = null;
    }

    private void FlushRemaining()
    {
        lock (_fileLock)
        {
            while (_queue.TryDequeue(out var line))
            {
                try
                {
                    _writer?.WriteLine(line);
                }
                catch { }
            }
            try { _writer?.Flush(); } catch { }
        }
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
                    if (new FileInfo(file).LastWriteTime.Date < cutoffDate)
                        File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _writeTask.Wait(3000); } catch { }
        lock (_fileLock) { CloseLogFile(); }
        _cts.Dispose();
    }
}

/// <summary>
/// JSON Lines日志Provider。所有Logger共享同一个Writer实例。
/// </summary>
public class JsonLineLoggerProvider : ILoggerProvider
{
    private readonly JsonLineLoggerWriter _writer;
    private readonly LogLevel _globalMinimumLevel;
    private readonly List<(string Prefix, LogLevel Level)> _categoryFilters;
    private bool _disposed;

    public JsonLineLoggerProvider(string basePath, string fileNamePrefix, int retentionDays = 7,
        LogLevel globalMinimumLevel = LogLevel.Information,
        Dictionary<string, LogLevel>? categoryFilters = null,
        int flushIntervalMs = 200)
    {
        _writer = new JsonLineLoggerWriter(basePath, fileNamePrefix, retentionDays, flushIntervalMs);
        _globalMinimumLevel = globalMinimumLevel;

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

    public JsonLineLoggerWriter Writer => _writer;

    public ILogger CreateLogger(string categoryName)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(JsonLineLoggerProvider));
        var minLevel = GetMinimumLevelForCategory(categoryName);
        return new JsonLineLogger(categoryName, minLevel, _writer);
    }

    private LogLevel GetMinimumLevelForCategory(string categoryName)
    {
        foreach (var (prefix, level) in _categoryFilters)
        {
            if (categoryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return level;
        }
        return _globalMinimumLevel;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Dispose();
    }
}

/// <summary>
/// JSON日志条目结构。字段名用短名减少文件体积。
/// </summary>
public class LogEntry
{
    /// <summary>时间戳 yyyy-MM-dd HH:mm:ss.fff</summary>
    public string Ts { get; set; } = "";

    /// <summary>级别: TRCE/DBG/INFO/WARN/ERR/CRIT</summary>
    public string Level { get; set; } = "";

    /// <summary>类别名 (CategoryName)</summary>
    public string Cat { get; set; } = "";

    /// <summary>日志消息</summary>
    public string Msg { get; set; } = "";

    /// <summary>EventId</summary>
    public int? Eid { get; set; }

    /// <summary>EventName</summary>
    public string? Ename { get; set; }

    /// <summary>结构化参数</summary>
    public Dictionary<string, object>? Props { get; set; }

    /// <summary>异常类型</summary>
    public string? Err { get; set; }

    /// <summary>异常消息</summary>
    public string? ErrMsg { get; set; }

    /// <summary>异常堆栈</summary>
    public string? Stack { get; set; }
}
