using Overglow.Devices.Drivers.File;

namespace Overglow.Devices.Tests;

public sealed class FileDriverTests
{
    [Fact]
    public void FormatLine_MatchesTheCppOutput()
    {
        var line = new char[128];

        var n = FileDriver.FormatLine(
            line,
            new DateTime(2026, 10, 19, 20, 15, 0, 123),
            TimeSpan.FromMilliseconds(16.66),
            new byte[] { 255, 0, 0, 1, 22, 133 });

        Assert.Equal("2026-10-19T20:15:00.123 | +16.7 [{255,0,0}{1,22,133}]" + Environment.NewLine, new string(line, 0, n));
    }

    [Fact]
    public void FormatLine_WithoutTimestamp()
    {
        var line = new char[64];

        var n = FileDriver.FormatLine(line, null, TimeSpan.Zero, new byte[] { 7, 8, 9 });

        Assert.Equal(" [{7,8,9}]" + Environment.NewLine, new string(line, 0, n));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/dev/null")]
    public void NullOutput_MapsToThePlatformNullDevice(string output) =>
        Assert.Equal(OperatingSystem.IsWindows() ? @"\\.\NUL" : "/dev/null", new FileDriverOptions(output).ResolvedPath);

    [Fact]
    public async Task Driver_WritesOneLinePerFrame()
    {
        var path = Path.Combine(Path.GetTempPath(), $"overglow-file-driver-{Guid.NewGuid():N}.txt");
        try
        {
            using (var driver = new FileDriver(new FileDriverOptions(path, PrintTimeStamp: false)))
            {
                await driver.OpenAsync(2, TestContext.Current.CancellationToken);
                Assert.True(driver.Write(new byte[] { 1, 2, 3, 4, 5, 6 }));
                Assert.True(driver.Write(new byte[] { 0, 0, 0, 0, 0, 0 }));
                await driver.CloseAsync(TestContext.Current.CancellationToken);
            }

            Assert.Equal([" [{1,2,3}{4,5,6}]", " [{0,0,0}{0,0,0}]"], await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Driver_CanWriteToTheNullDevice()
    {
        using var driver = new FileDriver(new FileDriverOptions("/dev/null"));
        await driver.OpenAsync(1, TestContext.Current.CancellationToken);

        Assert.True(driver.Write(new byte[] { 1, 2, 3 }));
    }
}
