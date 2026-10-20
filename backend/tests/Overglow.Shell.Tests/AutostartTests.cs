using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using Overglow.App;
using Overglow.Platform.Windows.Desktop;

namespace Overglow.Shell.Tests;

/// <summary>
/// The Run key logic against a scratch key under HKCU\Software\Overglow.Tests (deleted afterwards); the real Run key
/// is never written by the tests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutostartTests : IDisposable
{
    private const string Executable = @"C:\Program Files\Overglow\overglow.exe";
    private const string TestsKey = @"Software\Overglow.Tests";
    private readonly string _key = $@"{TestsKey}\{Guid.NewGuid():N}\Run";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Path.GetDirectoryName(_key)!, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(TestsKey))
        {
            if (parent is null || parent.SubKeyCount > 0)
            {
                return;
            }
        }

        // the last test class instance removes the scratch root
        try
        {
            Registry.CurrentUser.DeleteSubKey(TestsKey, throwOnMissingSubKey: false);
        }
        catch (InvalidOperationException)
        {
            // another test created a key in between
        }
    }

    private RegistryAutostart Create(params string[] arguments)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.NotEqual(RegistryAutostart.RunKey, _key);
        return new RegistryAutostart(Registry.CurrentUser, _key, Executable, arguments);
    }

    [Fact]
    public void Enable_WritesAQuotedCommand_DisableRemovesIt()
    {
        var autostart = Create("--no-window", "--data-dir", @"D:\Overglow data");
        Assert.False(autostart.IsEnabled);

        autostart.SetEnabled(true);
        Assert.True(autostart.IsEnabled);
        using (var key = Registry.CurrentUser.OpenSubKey(_key))
        {
            Assert.Equal(@"""C:\Program Files\Overglow\overglow.exe"" --no-window --data-dir ""D:\Overglow data""", key!.GetValue(RegistryAutostart.ValueName));
        }

        autostart.SetEnabled(false);
        Assert.False(autostart.IsEnabled);
        using (var key = Registry.CurrentUser.OpenSubKey(_key))
        {
            Assert.Null(key!.GetValue(RegistryAutostart.ValueName));
        }

        autostart.SetEnabled(false);
    }

    [Fact]
    public void AnEntryForAnotherProgram_DoesNotCountAsEnabled()
    {
        var autostart = Create();
        using (var key = Registry.CurrentUser.CreateSubKey(_key))
        {
            key.SetValue(RegistryAutostart.ValueName, @"""C:\Other\overglow.exe"" --no-window");
        }

        Assert.False(autostart.IsEnabled);
        autostart.SetEnabled(true);
        Assert.True(autostart.IsEnabled);
    }

    [Fact]
    public void TheCppEntry_ABarePath_IsRecognized()
    {
        var autostart = Create();
        Assert.True(autostart.StartsThisExecutable(Executable));
        Assert.True(autostart.StartsThisExecutable(Executable.ToUpperInvariant() + " --no-window"));
        Assert.False(autostart.StartsThisExecutable(Executable + ".old"));
    }

    [Fact]
    public void Arguments_StartInTheTray_WithTheDataDirectoryUnlessDefault()
    {
        var options = AppOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["data-dir"] = @"D:\data" }).Build());
        Assert.Equal(["--no-window", "--data-dir", Path.GetFullPath(@"D:\data")], DesktopRegistration.AutostartArguments(options));
    }
}
