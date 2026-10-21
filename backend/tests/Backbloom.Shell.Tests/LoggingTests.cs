using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Backbloom.App.Logging;

namespace Backbloom.Shell.Tests;

public sealed class LoggingTests : IDisposable
{
    private readonly string _dir = TestHost.NewDirectory();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 21, 0, 0, TimeSpan.Zero));

    public LoggingTests() => _time.SetLocalTimeZone(TimeZoneInfo.Utc);

    public void Dispose() => TestHost.DeleteDirectory(_dir);

    [Fact]
    public void Lines_HaveTimeLevelCategoryAndException()
    {
        using (var provider = new RollingFileLoggerProvider(_dir, _time))
        {
            var logger = provider.CreateLogger("Backbloom.Test");
            Log.Hello(logger, "world");
            Log.Failed(logger, new InvalidOperationException("boom"));
            provider.Flush();
        }

        var lines = File.ReadAllLines(Path.Combine(_dir, "backbloom-20261019.log"));
        Assert.Equal("2026-10-19 21:00:00.000 INFO  Backbloom.Test: Hello world", lines[0]);
        Assert.Equal("2026-10-19 21:00:00.000 ERROR Backbloom.Test: Failed", lines[1]);
        Assert.StartsWith("    System.InvalidOperationException: boom", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void CrashReport_AppendsAFatalEntryToTheDaysLog_WhileTheLoggerHasItOpen()
    {
        var logs = Path.Combine(_dir, "logs");
        string? path;
        using (var provider = new RollingFileLoggerProvider(logs, _time))
        {
            var logger = provider.CreateLogger("Backbloom.Test");
            Log.Hello(logger, "world");
            provider.Flush();
            path = CrashReport.Write(_dir, "Backbloom couldn't start", new InvalidDataException("settings.db isn't readable"), time: _time);
        }

        Assert.Equal(Path.Combine(logs, "backbloom-20261019.log"), path);
        var lines = File.ReadAllLines(path!);
        Assert.Equal("2026-10-19 21:00:00.000 INFO  Backbloom.Test: Hello world", lines[0]);
        Assert.Equal("2026-10-19 21:00:00.000 FATAL Backbloom: Backbloom couldn't start", lines[1]);
        Assert.StartsWith("    System.IO.InvalidDataException: settings.db isn't readable", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void CrashReport_CreatesTheLogFolderWhenNothingWasLoggedYet()
    {
        var path = CrashReport.Write(Path.Combine(_dir, "new"), "Backbloom couldn't start", new InvalidOperationException("boom"), time: _time);

        Assert.Equal(Path.Combine(_dir, "new", "logs", "backbloom-20261019.log"), path);
        Assert.Contains("FATAL Backbloom: Backbloom couldn't start", File.ReadAllText(path!), StringComparison.Ordinal);
    }

    [Fact]
    public void Files_RollByDayAndSize_AndOnlyTheNewestAreKept()
    {
        for (var i = 0; i < 12; i++)
        {
            var old = Path.Combine(_dir, $"backbloom-202609{10 + i}.log");
            File.WriteAllText(old, "old");
            File.SetLastWriteTimeUtc(old, new DateTime(2026, 9, 10 + i, 0, 0, 0, DateTimeKind.Utc));
        }

        using (var provider = new RollingFileLoggerProvider(_dir, _time, maxFileBytes: 200))
        {
            var logger = provider.CreateLogger("Backbloom.Test");
            for (var i = 0; i < 6; i++)
            {
                Log.Long(logger, i);
                provider.Flush();
            }

            _time.Advance(TimeSpan.FromDays(1));
            Log.NextDay(logger);
            provider.Flush();
            Assert.EndsWith("backbloom-20261020.log", provider.CurrentPath, StringComparison.Ordinal);
        }

        var files = Directory.GetFiles(_dir, "backbloom-*.log").Select(Path.GetFileName).ToList();
        Assert.Equal(RollingFileLoggerProvider.RetainedFiles, files.Count);
        Assert.Contains("backbloom-20261019.log", files);
        Assert.Contains("backbloom-20261019-1.log", files);
        Assert.Contains("backbloom-20261020.log", files);
        Assert.DoesNotContain("backbloom-20260910.log", files);
    }

    [Fact]
    public void CrashReport_WritesTheRecentPerformanceSamples_AndNamesTheFileInTheEntry()
    {
        var path = CrashReport.Write(_dir, "Backbloom stopped after an unexpected error", new InvalidOperationException("boom"), ["""{"v":1}""", """{"v":2}"""], _time);

        var performance = Path.Combine(_dir, "logs", "crash-20261019-210000.jsonl");
        Assert.Equal("{\"v\":1}\n{\"v\":2}\n", File.ReadAllText(performance));
        Assert.Contains("FATAL Backbloom: Backbloom stopped after an unexpected error (performance before it: crash-20261019-210000.jsonl)", File.ReadAllText(path!), StringComparison.Ordinal);
    }

    [Fact]
    public void CrashReport_WritesTheEntry_EvenWhenTheSamplesCantBeWritten()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "logs", "crash-20261019-210000.jsonl"));

        var path = CrashReport.Write(_dir, "Backbloom stopped after an unexpected error", new InvalidOperationException("boom"), ["""{"v":1}"""], _time);

        Assert.Contains("FATAL Backbloom: Backbloom stopped after an unexpected error", File.ReadAllText(path!), StringComparison.Ordinal);
    }

    [Fact]
    public void PerformanceFile_KeepsWriting_WhenAnOldFileCantBeDeleted()
    {
        var old = Path.Combine(_dir, "performance-20260901.jsonl");
        File.WriteAllText(old, "old");
        File.SetLastWriteTimeUtc(old, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        for (var i = 0; i < PerformanceFile.RetainedFiles; i++)
        {
            File.WriteAllText(Path.Combine(_dir, $"performance-202609{10 + i}.jsonl"), "newer");
        }

        File.SetAttributes(old, FileAttributes.ReadOnly);
        try
        {
            var file = new PerformanceFile(_dir, _time);
            file.Append("""{"a":1}""");
            file.Append("""{"a":2}""");
            Assert.Equal("{\"a\":1}\n{\"a\":2}\n", File.ReadAllText(Path.Combine(_dir, "performance-20261019.jsonl")));
            Assert.True(File.Exists(old));
        }
        finally
        {
            File.SetAttributes(old, FileAttributes.Normal);
        }
    }

    [Fact]
    public void PerformanceFile_AppendsOneLinePerSample_ANewFilePerDay_AndKeepsOnlyTheNewest()
    {
        for (var i = 0; i < 20; i++)
        {
            var old = Path.Combine(_dir, $"performance-202609{10 + i}.jsonl");
            File.WriteAllText(old, "old");
            File.SetLastWriteTimeUtc(old, new DateTime(2026, 9, 10 + i, 0, 0, 0, DateTimeKind.Utc));
        }

        var file = new PerformanceFile(_dir, _time);
        file.Append("""{"a":1}""");
        file.Append("""{"a":2}""");
        Assert.Equal(Path.Combine(_dir, "performance-20261019.jsonl"), file.CurrentPath);
        _time.Advance(TimeSpan.FromDays(1));
        file.Append("""{"a":3}""");

        Assert.Equal("{\"a\":1}\n{\"a\":2}\n", File.ReadAllText(Path.Combine(_dir, "performance-20261019.jsonl")));
        Assert.Equal("{\"a\":3}\n", File.ReadAllText(Path.Combine(_dir, "performance-20261020.jsonl")));
        var files = Directory.GetFiles(_dir, PerformanceFile.FilePattern).Select(Path.GetFileName).ToList();
        Assert.Equal(PerformanceFile.RetainedFiles, files.Count);
        Assert.Contains("performance-20260929.jsonl", files);
        Assert.DoesNotContain("performance-20260917.jsonl", files);
    }

    [Theory]
    [InlineData("silent", LogLevel.Error)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("verbose", LogLevel.Information)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData(null, LogLevel.Information)]
    public void Level_FollowsTheLoggerSetting(string? level, LogLevel expected)
    {
        var setting = new LogLevelSetting();
        setting.Apply(level is null ? null : new JsonObject { ["level"] = level });
        Assert.Equal(expected, setting.Minimum);
    }

    [Fact]
    public async Task TheHost_WritesItsLogToTheDataDirectory()
    {
        await using var host = await TestHost.StartAsync("""{ "logger": { "level": "verbose" } }""");
        var setting = (LogLevelSetting)host.App.Services.GetService(typeof(LogLevelSetting))!;
        Assert.Equal(LogLevel.Information, setting.Minimum);
        host.Settings.Update(new JsonObject { ["logger"] = new JsonObject { ["level"] = "warn" } });
        Assert.Equal(LogLevel.Warning, setting.Minimum);

        var logs = Path.Combine(host.DataDirectory, "logs");
        Wait.Until(() => Directory.GetFiles(logs, "backbloom-*.log").Any(f => Read(f).Contains("Lighting started", StringComparison.Ordinal)));

        static string Read(string path)
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return reader.ReadToEnd();
        }
    }
}

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Hello {Name}")]
    public static partial void Hello(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed")]
    public static partial void Failed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A message long enough to fill the small test file quickly, number {Number}")]
    public static partial void Long(ILogger logger, int number);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Next day")]
    public static partial void NextDay(ILogger logger);
}
