using Baihua.Web.Services;

namespace Baihua.Web.Logging;

/// <summary>
/// 错误日志 LoggerProvider - 将 Error/Critical 级别的日志存入内存
/// </summary>
public class ErrorLoggerProvider : ILoggerProvider
{
    private readonly ErrorLogService _errorLogService;
    private readonly LogLevel _minLevel;

    public ErrorLoggerProvider(ErrorLogService errorLogService, LogLevel minLevel = LogLevel.Warning)
    {
        _errorLogService = errorLogService;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new ErrorLogger(categoryName, _errorLogService, _minLevel);
    }

    public void Dispose()
    {
        // 无需清理
    }
}

/// <summary>
/// 错误日志 Logger 实现
/// </summary>
public class ErrorLogger : ILogger
{
    private readonly string _categoryName;
    private readonly ErrorLogService _errorLogService;
    private readonly LogLevel _minLevel;

    public ErrorLogger(string categoryName, ErrorLogService errorLogService, LogLevel minLevel)
    {
        _categoryName = categoryName;
        _errorLogService = errorLogService;
        _minLevel = minLevel;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= _minLevel;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        // 只记录 Warning 及以上级别
        if (logLevel < LogLevel.Warning)
            return;

        var message = formatter(state, exception);
        
        var entry = new ErrorLogEntry
        {
            Timestamp = DateTime.UtcNow,
            Category = _categoryName,
            Level = logLevel,
            Message = message,
            Exception = exception?.GetType().FullName,
            StackTrace = exception?.StackTrace
        };

        _errorLogService.LogError(entry);
    }
}

/// <summary>
/// 扩展方法
/// </summary>
public static class ErrorLoggerExtensions
{
    /// <summary>
    /// 添加内存错误日志收集器（记录 Warning/Error/Critical 到内存，供健康检查页查看）
    /// </summary>
    public static ILoggingBuilder AddErrorLogCollector(this ILoggingBuilder builder, LogLevel minLevel = LogLevel.Warning)
    {
        builder.Services.AddSingleton<ILoggerProvider>(sp =>
        {
            var errorLogService = sp.GetRequiredService<ErrorLogService>();
            return new ErrorLoggerProvider(errorLogService, minLevel);
        });
        return builder;
    }
}
