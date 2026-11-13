using Microsoft.Extensions.Logging;
using Backbloom.Server.Logging;

namespace Backbloom.Server.Tests;

/// <summary>The recent log lines the UI's log view shows, and the live feed.</summary>
public sealed class LogBufferTests
{
    [Fact]
    public void Buffer_KeepsTheNewestLines()
    {
        var buffer = new LogBuffer(capacity: 2);
        var fed = new List<string>();
        buffer.Added += e => fed.Add(e.Message);

        foreach (var message in new[] { "one", "two", "three" })
        {
            buffer.Add(new LogEntry("TEST", LogLevel.Information, message, DateTimeOffset.UnixEpoch));
        }

        Assert.Equal(["two", "three"], buffer.Recent().Select(e => e.Message));
        Assert.Equal(["one", "two", "three"], fed);
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogBuffer(0));
    }

    [Theory]
    [InlineData(LogLevel.Trace, "DEBUG")]
    [InlineData(LogLevel.Debug, "DEBUG")]
    [InlineData(LogLevel.Information, "INFO")]
    [InlineData(LogLevel.Warning, "WARNING")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Critical, "ERROR")]
    public void Levels_HaveHyperHdrsNames(LogLevel level, string name) =>
        Assert.Equal(name, new LogEntry("TEST", level, "m", DateTimeOffset.UnixEpoch).LevelString);

    [Fact]
    public void Provider_FiltersByLevel_NamesTheCategoryShortly_AndAppendsTheException()
    {
        var buffer = new LogBuffer();
        using var provider = new LogBufferProvider(buffer, LogLevel.Warning);
        var logger = provider.CreateLogger("Backbloom.Devices.LedDeviceRunner");

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.False(logger.IsEnabled(LogLevel.None));
        Assert.Null(logger.BeginScope("scope"));
#pragma warning disable CA1848, CA2254 // the provider itself is under test
        logger.LogInformation("not shown");
        logger.LogWarning("shown");
        logger.LogError(new IOException("controller unreachable"), "Couldn't open");
#pragma warning restore CA1848, CA2254

        Assert.Equal(
            [("LEDDEVICERUNNER", LogLevel.Warning, "shown"), ("LEDDEVICERUNNER", LogLevel.Error, "Couldn't open: controller unreachable")],
            buffer.Recent().Select(e => (e.LoggerName, e.Level, e.Message)));

        // a category without a namespace is used as it is
        var plain = provider.CreateLogger("Plain");
#pragma warning disable CA1848, CA2254
        plain.LogWarning("plain");
#pragma warning restore CA1848, CA2254
        Assert.Equal("PLAIN", buffer.Recent()[^1].LoggerName);
    }
}
