using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Overglow.Capture.Windows.Tests;

/// <summary>Collects log messages so failing tests can show what the capture code reported.</summary>
internal sealed class TestLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages;

    public override string ToString() => string.Join(Environment.NewLine, _messages);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _messages.Enqueue($"{logLevel}: {formatter(state, exception)}{(exception is null ? "" : " " + exception.Message)}");
    }
}
