using Overglow.App;

namespace Overglow.Shell.Tests;

public sealed class CommandLineTests
{
    private static CommandLine Parse(params string[] args)
    {
        var (line, error) = CommandLine.Parse(args);
        Assert.Null(error);
        return line!;
    }

    private static string Error(params string[] args)
    {
        var (line, error) = CommandLine.Parse(args);
        Assert.Null(line);
        return error!;
    }

    [Fact]
    public void Settings_BecomeConfigurationArguments()
    {
        var line = Parse("--data-dir", @"C:\data", "--port=18890", "--capture", "synthetic", "--bind", "0.0.0.0", "--fps", "30");
        Assert.Equal(LaunchMode.Run, line.Mode);
        Assert.Equal(["--data-dir", @"C:\data", "--port", "18890", "--capture", "synthetic", "--bind", "0.0.0.0", "--fps", "30"], line.ConfigurationArgs);
        Assert.False(line.NoWindow);
        Assert.False(line.Service);
    }

    [Fact]
    public void Modes_AndFlags()
    {
        Assert.Equal(LaunchMode.Version, Parse("--version").Mode);
        Assert.Equal(LaunchMode.Help, Parse("-h").Mode);
        Assert.Equal(LaunchMode.Help, Parse("--help").Mode);
        Assert.Equal(LaunchMode.Quit, Parse("--quit", "--data-dir", "x").Mode);
        var import = Parse("--import", "hyperhdr.db", "--data-dir", "x");
        Assert.Equal(LaunchMode.Import, import.Mode);
        Assert.Equal("hyperhdr.db", import.ImportFile);
        Assert.Equal(["--data-dir", "x"], import.ConfigurationArgs);
        Assert.True(Parse("--no-window").NoWindow);
        Assert.True(Parse("--service").Service);
    }

    [Theory]
    [InlineData("Unknown option --bogus.", "--bogus")]
    [InlineData("Unexpected argument 'import'.", "import", "x.db")]
    [InlineData("--port must be a number from 0 to 65535, not '70000'.", "--port", "70000")]
    [InlineData("--port must be a number from 0 to 65535, not '-1'.", "--port=-1")]
    [InlineData("--port needs a value.", "--port")]
    [InlineData("--bind must be an IP address, not 'localhost'.", "--bind", "localhost")]
    [InlineData("--capture must be 'screen' or 'synthetic', not 'camera'.", "--capture", "camera")]
    [InlineData("--capture must be 'screen' or 'synthetic', not '1'.", "--capture", "1")]
    [InlineData("--fps must be a number above 0 and up to 1000, not '0'.", "--fps", "0")]
    [InlineData("--port is given twice.", "--port", "1", "--port", "2")]
    [InlineData("--service takes no value.", "--service=yes")]
    [InlineData("Use only one of --import, --quit, --version and --help.", "--quit", "--version")]
    [InlineData("--no-window and --service can't be combined (--service has no window anyway).", "--no-window", "--service")]
    public void Errors_AreReported(string expected, params string[] args) => Assert.Equal(expected, Error(args));

    [Fact]
    public void Usage_ListsEveryOption()
    {
        foreach (var option in new[] { "--data-dir", "--legacy-data-dir", "--port", "--bind", "--ui-dir", "--capture", "--fps", "--lut-dirs", "--no-window", "--service", "--import", "--quit", "--version", "--help" })
        {
            Assert.Contains(option, CommandLine.Usage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Version_NamesProductAndRuntime()
    {
        var text = CommandLine.VersionText("1.2.3", "abcdef12");
        Assert.StartsWith("Overglow 1.2.3 (abcdef12)", text, StringComparison.Ordinal);
        Assert.Contains(".NET ", text, StringComparison.Ordinal);
    }
}
