using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Backbloom.App;
using Backbloom.Capture;

namespace Backbloom.Shell.Tests;

/// <summary>
/// Composition decisions of the app that the running host doesn't show directly: which hosts may present certificates
/// that don't validate, the frame source <c>--capture</c> chooses, and the <c>--import</c> command.
/// </summary>
public sealed class CompositionTests : IDisposable
{
    private readonly string _dir = TestHost.NewDirectory();

    public void Dispose() => TestHost.DeleteDirectory(_dir);

    /// <summary>
    /// LED controllers on the local network (Hue bridges, Nanoleaf) use self-signed certificates, so a certificate that
    /// doesn't validate is accepted for private, link-local, loopback and ".local" hosts only.
    /// </summary>
    [Theory]
    [InlineData("hue-bridge.local", true)]
    [InlineData("HUE.LOCAL", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("[fe80::1]", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::1", true)]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.169.1.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("example.com", false)]
    [InlineData("local", false)]
    [InlineData("hue.local.example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LanHttps_AcceptsUnverifiedCertificatesOnlyFromLocalHosts(string? host, bool local) =>
        Assert.Equal(local, DeviceRegistration.IsLocalHost(host));

    private static AppOptions Options(params (string Key, string Value)[] values) =>
        AppOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build());

    private static IFrameSource SourceFor(AppOptions options)
    {
        using var services = new ServiceCollection().AddLogging().AddCapture(options).BuildServiceProvider();
        return services.GetRequiredService<IFrameSource>();
    }

    [Fact]
    public void Capture_APictureIsShownAsItIs_TheTestPatternOtherwise()
    {
        var picture = Path.Combine(_dir, "room.ppm");
        File.WriteAllBytes(picture, [.. "P6\n2 1\n255\n"u8, 255, 0, 0, 0, 0, 255]);

        Assert.IsType<ImageFrameSource>(SourceFor(Options(("data-dir", _dir), ("capture-image", picture))));
        Assert.IsType<SyntheticFrameSource>(SourceFor(Options(("data-dir", _dir), ("capture", "synthetic"))));
    }

    [Fact]
    public void Import_ReplacesTheSettings_AndSavesThePreviousOnesFirst()
    {
        string[] args = ["--data-dir", _dir, "--legacy-data-dir", "none"];
        var database = Path.Combine(AppContext.BaseDirectory, "fixtures", "settings", "reference-hyperhdr-v22.db");

        Assert.Equal(0, BackbloomHost.Import(database, args));
        Assert.Empty(Backups());

        // the second import replaces imported settings: they are saved before
        Assert.Equal(0, BackbloomHost.Import(database, args));
        Assert.Single(Backups());
    }

    [Fact]
    public void Import_OfAFileThatIsntAHyperHdrDatabase_Fails()
    {
        string[] args = ["--data-dir", _dir, "--legacy-data-dir", "none"];
        var notADatabase = Path.Combine(_dir, "notes.db");
        File.WriteAllText(notADatabase, "not a database");

        Assert.Equal(1, BackbloomHost.Import(Path.Combine(_dir, "missing.db"), args));
        Assert.Equal(1, BackbloomHost.Import(notADatabase, args));
        Assert.Throws<ArgumentException>(() => BackbloomHost.Import(" ", args));
    }

    private string[] Backups()
    {
        var backups = Path.Combine(_dir, "backups");
        return Directory.Exists(backups) ? Directory.GetFiles(backups) : [];
    }
}
