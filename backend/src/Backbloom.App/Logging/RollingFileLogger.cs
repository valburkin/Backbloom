using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Backbloom.App.Logging;

/// <summary>
/// Writes the log to <c>logs/backbloom-yyyyMMdd.log</c> in the data directory: one line per message (local time,
/// level, category, the rendered message template, then the exception), a new file per day and whenever a file
/// reaches <see cref="MaxFileBytes"/> (<c>-1</c>, <c>-2</c>, ...), and only the newest <see cref="RetainedFiles"/> files
/// are kept. Messages are queued and written by one background thread, so logging never waits for the disk; when the
/// queue is full (the disk stalls) new messages are dropped and counted.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int RetainedFiles = 10;

    private readonly BlockingCollection<Entry> _queue = new(new ConcurrentQueue<Entry>(), 10_000);
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly long _maxFileBytes;
    private readonly Thread _writer;
    private StreamWriter? _file;
    private string? _day;
    private int _part;
    private long _dropped;

    public RollingFileLoggerProvider(string directory, TimeProvider? time = null, long maxFileBytes = MaxFileBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
        _time = time ?? TimeProvider.System;
        _maxFileBytes = maxFileBytes;
        Directory.CreateDirectory(directory);
        // normal priority: below it, a PC at full load (a game, a build) starved the writer for seconds, so Flush and
        // Dispose gave up and the last lines of a shutdown could go missing; the writer is idle almost all the time
        _writer = new Thread(WriteLoop) { Name = "Backbloom log file", IsBackground = true };
        _writer.Start();
    }

    /// <summary>The file written now (null before the first message).</summary>
    public string? CurrentPath { get; private set; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>Writes everything queued so far (tests, shutdown).</summary>
    public void Flush()
    {
        using var done = new ManualResetEventSlim();
        try
        {
            if (!_queue.TryAdd(new Entry(null, done), TimeSpan.FromSeconds(1)))
            {
                return;
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }

        done.Wait(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (_queue.IsAddingCompleted)
        {
            return;
        }

        // the writer closes the file when it has written the rest, also after this gives up waiting
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(5));
    }

    internal static string Format(DateTimeOffset time, LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder(64 + message.Length);
        line.Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(level switch
            {
                LogLevel.Trace => "TRACE",
                LogLevel.Debug => "DEBUG",
                LogLevel.Information => "INFO ",
                LogLevel.Warning => "WARN ",
                LogLevel.Error => "ERROR",
                _ => "FATAL",
            })
            .Append(' ')
            .Append(category)
            .Append(": ")
            .Append(message.ReplaceLineEndings(" "));
        if (exception is not null)
        {
            line.AppendLine().Append("    ").Append(exception.ToString().ReplaceLineEndings(Environment.NewLine + "    "));
        }

        return line.ToString();
    }

    private void Enqueue(string line)
    {
        try
        {
            if (!_queue.TryAdd(new Entry(line, null)))
            {
                Interlocked.Increment(ref _dropped);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // logged while shutting down, after the writer stopped
        }
    }

    private void WriteLoop()
    {
        foreach (var (line, flushed) in _queue.GetConsumingEnumerable())
        {
            if (line is null)
            {
                _file?.Flush();
                flushed?.Set();
                continue;
            }

            try
            {
                Write(line);
                if (_queue.Count == 0)
                {
                    _file?.Flush();
                }
            }
            catch (IOException)
            {
                // a full or vanished disk: keep running without the file
                _file?.Dispose();
                _file = null;
            }
            catch (UnauthorizedAccessException)
            {
                _file?.Dispose();
                _file = null;
            }
        }

        _file?.Dispose();
        _file = null;
    }

    private void Write(string line)
    {
        var day = _time.GetLocalNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_file is null || day != _day || _file.BaseStream.Length >= _maxFileBytes)
        {
            Open(day);
        }

        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
        {
            _file!.WriteLine($"({dropped} log messages were dropped: the disk was too slow)");
        }

        _file!.WriteLine(line);
    }

    private void Open(string day)
    {
        _file?.Dispose();
        _part = day == _day ? _part + 1 : 0;
        _day = day;
        string path;
        while (true)
        {
            path = Path.Combine(_directory, _part == 0 ? $"backbloom-{day}.log" : $"backbloom-{day}-{_part}.log");
            if (!File.Exists(path) || new FileInfo(path).Length < _maxFileBytes)
            {
                break;
            }

            _part++;
        }

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _file = new StreamWriter(stream, new UTF8Encoding(false));
        CurrentPath = path;
        DeleteOldFiles();
    }

    private void DeleteOldFiles()
    {
        var old = new DirectoryInfo(_directory).GetFiles("backbloom-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(RetainedFiles);
        foreach (var file in old)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // still open somewhere; next time
            }
        }
    }

    /// <summary>A line to write, or (no line) a flush request to confirm.</summary>
    private readonly record struct Entry(string? Line, ManualResetEventSlim? Flushed);

    private sealed class FileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Enqueue(Format(provider._time.GetLocalNow(), logLevel, category, formatter(state, exception), exception));
        }
    }
}
