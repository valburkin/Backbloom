using Microsoft.Win32;
using Overglow.Platform.Windows.Desktop;
using Overglow.Settings;
using Velopack;

namespace Overglow.App.Updates;

/// <summary>
/// Velopack's install, update and uninstall hooks, run by <c>VelopackApp.Run</c> first in <c>Main</c>: Velopack starts
/// the program with its own arguments for them and ends the process afterwards. A downloaded update is applied there
/// too, before anything else starts.
/// </summary>
internal static class InstallerHooks
{
    /// <summary>Only an installed copy calls Velopack (it writes a log to the user's profile as soon as it runs).</summary>
    public static bool IsInstalledCopy => InstalledLayout.IsInstalled(AppContext.BaseDirectory);

    /// <summary>
    /// Quits the running app (so the LEDs are switched off rather than the process killed) and removes the autostart
    /// entry. The data directory (settings, logs) is kept.
    /// </summary>
    public static void BeforeUninstall(SemanticVersion version)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        try
        {
            Launcher.QuitRunningAsync(DataDirectories.DefaultDataDirectory()).Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // Velopack ends the remaining processes itself.
        }

        var executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "overglow.exe");
        var autostart = new RegistryAutostart(Registry.CurrentUser, RegistryAutostart.RunKey, executable, []);
        if (autostart.IsEnabled)
        {
            autostart.SetEnabled(false);
        }
    }
}
