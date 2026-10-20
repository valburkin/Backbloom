using Microsoft.Extensions.Logging;

namespace Backbloom.Server.Logging;

/// <summary>One log line as the UI's log view shows it.</summary>
public sealed record LogEntry(string LoggerName, LogLevel Level, string Message, DateTimeOffset Time)
{
    /// <summary>HyperHDR's level names (the UI colors lines by them).</summary>
    public string LevelString => Level switch
    {
        LogLevel.Trace or LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        _ => "ERROR",
    };
}

/// <summary>The most recent log lines plus a live feed, for the <c>logging</c> command.</summary>
public sealed class LogBuffer
{
    private readonly Lock _lock = new();
    private readonly Queue<LogEntry> _entries = new();
    private readonly int _capacity;

    public LogBuffer(int capacity = 300)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    /// <summary>Raised for every new line (on the logging thread; handlers must be quick).</summary>
    public event Action<LogEntry>? Added;

    public IReadOnlyList<LogEntry> Recent()
    {
        lock (_lock)
        {
            return [.. _entries];
        }
    }

    public void Add(LogEntry entry)
    {
        lock (_lock)
        {
            if (_entries.Count == _capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }

        Added?.Invoke(entry);
    }
}

/// <summary>Feeds <see cref="LogBuffer"/> from Microsoft.Extensions.Logging.</summary>
public sealed class LogBufferProvider(LogBuffer buffer, LogLevel minimumLevel = LogLevel.Information) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BufferLogger(buffer, ShortName(categoryName), minimumLevel);

    public void Dispose()
    {
    }

    private static string ShortName(string category)
    {
        var dot = category.LastIndexOf('.');
        return (dot >= 0 ? category[(dot + 1)..] : category).ToUpperInvariant();
    }

    private sealed class BufferLogger(LogBuffer buffer, string name, LogLevel minimumLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message = $"{message}: {exception.Message}";
            }

            buffer.Add(new LogEntry(name, logLevel, message, DateTimeOffset.Now));
        }
    }
}
