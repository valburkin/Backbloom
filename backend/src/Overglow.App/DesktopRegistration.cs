using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Overglow.Engine;
using Overglow.Engine.Platform;
using Overglow.Platform.Windows.Desktop;
using Overglow.Settings;
using Overglow.Shell;

namespace Overglow.App;

/// <summary>
/// The desktop integration: power and session events with their policy (every mode), and with
/// <see cref="LaunchOptions.Desktop"/> the tray icon, the window and autostart. Windows only today; other platforms
/// get no events and run headless.
/// </summary>
internal static class DesktopRegistration
{
    /// <summary>The WebView2 profile (cache, local storage) in the data directory.</summary>
    public const string WebViewFolderName = "webview";

    public static IServiceCollection AddDesktop(this IServiceCollection services, AppOptions options, LaunchOptions launch)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            AddWindows(services, options, launch);
        }
        else
        {
            services.AddSingleton<ISystemEvents, NoSystemEvents>();
        }

        services.AddSingleton(sp => new PowerPolicy(
            sp.GetRequiredService<ISystemEvents>(),
            sp.GetRequiredService<LightingEngine>().Components,
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<PowerPolicy>>()));
        services.AddHostedService<ShellHostedService>();
        return services;
    }

    /// <summary>Arguments of the autostart entry: start in the tray, with this data directory unless it is the default.</summary>
    public static IReadOnlyList<string> AutostartArguments(AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var isDefault = string.Equals(
            Path.GetFullPath(options.DataDirectory).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(DataDirectories.DefaultDataDirectory()).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        return isDefault ? ["--no-window"] : ["--no-window", "--data-dir", options.DataDirectory];
    }

    [SupportedOSPlatform("windows10.0.17763")]
    private static void AddWindows(IServiceCollection services, AppOptions options, LaunchOptions launch)
    {
        services.AddSingleton<UiThread>();
        services.AddSingleton<WindowsSystemEvents>();
        services.AddSingleton<ISystemEvents>(sp => sp.GetRequiredService<WindowsSystemEvents>());
        if (!launch.Desktop)
        {
            return;
        }

        services.AddSingleton<IBrowserLauncher, SystemBrowserLauncher>();
        services.AddSingleton<IAutostart>(_ => new RegistryAutostart(
            Registry.CurrentUser, RegistryAutostart.RunKey, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "overglow.exe"), AutostartArguments(options)));
        services.AddSingleton<ITrayIcon, WindowsTrayIcon>();
        services.AddSingleton<IAppWindow>(sp => new WebViewWindow(
            sp.GetRequiredService<UiThread>(),
            AppUrls.Ui(options),
            new WindowPlacementStore(options.DataDirectory),
            Path.Combine(options.DataDirectory, WebViewFolderName),
            sp.GetRequiredService<IBrowserLauncher>(),
            sp.GetRequiredService<ILogger<WebViewWindow>>()));
        services.AddSingleton(sp => new TrayMenu(
            sp.GetRequiredService<LightingEngine>(),
            sp.GetRequiredService<IAppWindow>(),
            sp.GetRequiredService<IAutostart>(),
            sp.GetRequiredService<IHostApplicationLifetime>().StopApplication,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<TrayMenu>>()));
    }
}

/// <summary>
/// Starts the power policy and, on the desktop, the tray icon; opens the window once the server listens (unless
/// <c>--no-window</c>, which is ignored until the first-run welcome was completed). The end of the Windows session
/// stops the host from here and waits for the LEDs.
/// </summary>
internal sealed class ShellHostedService(
    IServiceProvider services,
    LaunchOptions launch,
    ISetupState setup,
    ISystemEvents events,
    GracefulShutdown shutdown,
    IHostApplicationLifetime lifetime) : IHostedService
{
    private PowerPolicy? _policy;
    private TrayMenu? _menu;
    private ITrayIcon? _tray;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _policy = services.GetRequiredService<PowerPolicy>();
        events.Raised += OnSystemEvent;
        if (launch.Desktop && services.GetService<TrayMenu>() is { } menu)
        {
            _menu = menu;
            _tray = services.GetRequiredService<ITrayIcon>();
            _tray.Show("Overglow", menu.Build, menu.OpenWindow);
            // the welcome must be seen: until the lighting was started once the window opens even with --no-window
            if (launch.OpenWindow || !setup.FirstRunCompleted)
            {
                lifetime.ApplicationStarted.Register(menu.OpenWindow);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        events.Raised -= OnSystemEvent;
        _menu?.Dispose();
        _tray?.Dispose();
        services.GetService<IAppWindow>()?.Dispose();
        _policy?.Dispose();
        return Task.CompletedTask;
    }

    private void OnSystemEvent(SystemEvent e)
    {
        if (e == SystemEvent.EndSession)
        {
            shutdown.StopAndWait("the Windows session ends");
        }
    }
}
