using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Backbloom.App;
using Backbloom.Platform.Windows.Desktop;

namespace Backbloom.Shell.Tests;

[SupportedOSPlatform("windows10.0.17763")]
public sealed class WindowTests : IDisposable
{
    private readonly string _dir = TestHost.NewDirectory();

    public void Dispose() => TestHost.DeleteDirectory(_dir);

    [Fact]
    public void Placement_IsSavedAndLoaded()
    {
        var store = new WindowPlacementStore(_dir);
        Assert.Null(store.Load());
        Assert.True(store.Save(new WindowPlacement(100, 50, 1280, 860, true)));
        Assert.Equal(new WindowPlacement(100, 50, 1280, 860, true), store.Load());
        Assert.DoesNotContain("isUsable", File.ReadAllText(store.Path), StringComparison.Ordinal);
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public void Placement_ThatCantBeWritten_IsReportedNotThrown()
    {
        var store = new WindowPlacementStore(_dir);
        Directory.CreateDirectory(store.Path);

        Assert.False(store.Save(new WindowPlacement(100, 50, 1280, 860, false)));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:privacy-microphone")]
    [InlineData("ftp://127.0.0.1/")]
    public void Browser_OpensOnlyWebPages(string url)
    {
        var started = new List<ProcessStartInfo>();
        var launcher = new SystemBrowserLauncher(start => { started.Add(start); return null; });
        Assert.Throws<ArgumentException>(() => launcher.Open(new Uri(url)));
        Assert.Empty(started);
    }

    [Fact]
    public void Browser_HandsAWebPageToTheSystem()
    {
        var started = new List<ProcessStartInfo>();
        new SystemBrowserLauncher(start => { started.Add(start); return null; }).Open(new Uri("https://github.com/valburkin/Backbloom/issues"));

        var start = Assert.Single(started);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("https://github.com/valburkin/Backbloom/issues", start.FileName);
            Assert.True(start.UseShellExecute);
        }
        else
        {
            Assert.Equal(["https://github.com/valburkin/Backbloom/issues"], start.ArgumentList);
        }
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "x": 0, "y": 0, "width": 50, "height": 50, "maximized": false }""")]
    [InlineData("")]
    public void Placement_BrokenOrTooSmall_IsIgnored(string content)
    {
        var store = new WindowPlacementStore(_dir);
        File.WriteAllText(store.Path, content);
        Assert.Null(store.Load());
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(20, 24)]
    [InlineData(32, 32)]
    [InlineData(40, 48)]
    [InlineData(256, 256)]
    [InlineData(400, 256)]
    public void Icon_TheBestImageIsChosen(int size, int expectedWidth)
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763), "Windows only");
        var ico = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "branding", "backbloom.ico"));
        var (offset, length) = AppIcon.SelectImage(ico, size);
        var widths = Enumerable.Range(0, BitConverter.ToUInt16(ico, 4)).Select(i => (Width: ico[6 + (i * 16)] == 0 ? 256 : ico[6 + (i * 16)], Offset: BitConverter.ToInt32(ico, 6 + (i * 16) + 12)));
        Assert.Equal(expectedWidth, widths.Single(w => w.Offset == offset).Width);
        Assert.True(offset + length <= ico.Length);
    }

    [Fact]
    public void Icon_CreatesAWin32Icon()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763), "Windows only");
        var icon = AppIcon.Create(32);
        Assert.NotEqual(0, (nint)icon);
        Assert.True(TerraFX.Interop.Windows.Windows.DestroyIcon(icon) != 0);
    }

    [Fact]
    public void UiUrl_IsReachableFromThisComputer()
    {
        static AppOptions With(string bind) => AppOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["bind"] = bind, ["port"] = "18890", ["data-dir"] = Path.GetTempPath() }).Build());

        Assert.Equal("http://127.0.0.1:18890/", AppUrls.Ui(With("127.0.0.1")).AbsoluteUri);
        Assert.Equal("http://127.0.0.1:18890/", AppUrls.Ui(With("0.0.0.0")).AbsoluteUri);
        Assert.Equal("http://[::1]:18890/", AppUrls.Ui(With("::")).AbsoluteUri);
        Assert.Equal("http://192.168.1.20:18890/", AppUrls.Ui(With("192.168.1.20")).AbsoluteUri);
    }

    [Fact]
    public void WebView2Runtime_IsInstalledOnTheDevelopmentMachine()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763), "Windows only");
        Assert.NotNull(WebViewWindow.RuntimeVersion());
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "branding")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("branding/ not found above the test output.");
    }
}
