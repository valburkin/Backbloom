using System.Globalization;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Backbloom.App.Logging;
using Backbloom.App.Updates;
using Backbloom.Devices;
using Backbloom.Engine;
using Backbloom.Engine.Diagnostics;
using Backbloom.Engine.Platform;
using Backbloom.Server;
using Backbloom.Server.Logging;
using Backbloom.Settings;
using Backbloom.Settings.Auth;
using Backbloom.Settings.Storage;

namespace Backbloom.App;

/// <summary>
/// The composition root: every service is created here and handed to its users through constructor injection
/// (no static singletons or service locators). Integration tests build the same host on a random port.
/// </summary>
public static class BackbloomHost
{
    /// <summary>Folder of the log files in the data directory.</summary>
    public const string LogDirectoryName = "logs";
    public static WebApplication Build(string[] args) => Build(args, null);

    /// <param name="args">Settings options of the command line (see <see cref="AppOptions"/>).</param>
    /// <param name="configure">Lets tests replace services (e.g. the frame source) before the host is built.</param>
    /// <param name="launch">Tray and window, console logging; default: headless with console logging (tests).</param>
    public static WebApplication Build(string[] args, Action<AppOptions, IServiceCollection>? configure, LaunchOptions? launch = null)
    {
        launch ??= LaunchOptions.Headless;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.AddEnvironmentVariables("BACKBLOOM_");
        var options = AppOptions.FromConfiguration(builder.Configuration);
        Directory.CreateDirectory(options.DataDirectory);

        // Loopback always (in code, never reloaded); the network endpoint only while remote access is on (RemoteAccess),
        // from configuration that Kestrel rebinds live. --bind fixes one address instead.
        var networkEndpoint = new NetworkEndpointConfiguration();
        builder.WebHost.UseKestrel(k =>
        {
            k.Listen(options.BindAddress ?? IPAddress.Loopback, options.Port);
            if (options.BindAddress is null)
            {
                k.Configure(networkEndpoint.Configuration, reloadOnChange: true);
            }
        });

        // Console (when there is one), the rolling file in the data directory and the UI's log view, all at the
        // level of the "logger" settings section.
        var logs = new LogBuffer();
        var level = new LogLevelSetting();
        builder.Logging.ClearProviders();
        if (launch.ConsoleLogging)
        {
            builder.Logging.AddSimpleConsole(ConfigureConsole);
        }

        builder.Logging.AddProvider(new RollingFileLoggerProvider(Path.Combine(options.DataDirectory, LogDirectoryName)));
        builder.Logging.AddProvider(new LogBufferProvider(logs));
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddFilter((_, _, logLevel) => logLevel >= level.Minimum);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton(launch);
        services.AddSingleton(logs);
        services.AddSingleton(level);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<GracefulShutdown>();

        // LED drivers and what they share (HTTP, discovery, MQTT).
        services.AddDevices();

        // Settings and credentials: settings.db in the data directory, imported from HyperHDR on the first start.
        services.AddSingleton(sp => CreateSchema(sp.GetRequiredService<DriverRegistry>()));
        services.AddSingleton(sp => SettingsServices.Open(
            new SettingsOptions(options.DataDirectory, options.LegacyDataDirectory), sp.GetRequiredService<SettingsSchema>(), sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<ISettingsStore>(sp => sp.GetRequiredService<SettingsServices>().Store);
        services.AddSingleton<IAuthStore>(sp => sp.GetRequiredService<SettingsServices>().Auth);
        services.AddSingleton<ISetupState>(sp => sp.GetRequiredService<SettingsServices>().Setup);
        services.AddSingleton(sp => CreateServerOptions(options, sp.GetRequiredService<SettingsServices>().Database));

        // Network access is opt-in (network.remoteAccess): loopback only until the user turns it on.
        services.AddSingleton(networkEndpoint);
        services.AddSingleton(RemoteAccessOptions.Default);
        services.AddSingleton<RemoteAccess>();
        services.AddSingleton<INetworkAccess>(sp => sp.GetRequiredService<RemoteAccess>());
        services.AddHostedService(sp => sp.GetRequiredService<RemoteAccess>());

        // The lighting: frame source -> pipeline -> controllers.
        services.AddPlatform();
        services.AddCapture(options);
        services.AddAudio();
        services.AddSingleton<DeviceManager>();
        services.AddSingleton<LightingEngine>();

        // No LED output before the first-run welcome is completed, nor while HyperHDR runs.
        services.AddSingleton<OtherAppMonitor>();
        services.AddSingleton(sp => new LightingGate(
            sp.GetRequiredService<LightingEngine>().Components,
            sp.GetRequiredService<ISetupState>(),
            sp.GetRequiredService<OtherAppMonitor>(),
            sp.GetRequiredService<ILogger<LightingGate>>()));

        // The layout's profiles: other roles for the strips while a program runs (or one chosen by hand).
        services.AddSingleton<ProfileMonitor>();

        // Registered before the engine so that it stops after it: the tray icon goes once the LEDs are off.
        services.AddDesktop(options, launch);
        services.AddHostedService<EngineHostedService>();

        // Performance samples for looking into problems afterwards (and for a crash report).
        var product = ProductVersion();
        services.AddSingleton(sp => new PerformanceRecorder(
            sp.GetRequiredService<LightingEngine>(),
            sp.GetRequiredKeyedService<ISystemMetrics>(PlatformRegistration.RecorderMetrics),
            sp.GetRequiredService<IGpuMetrics>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<PerformanceRecorder>>(),
            $"{product.Version}+{product.Build}"));
        services.AddSingleton(sp => new PerformanceFile(Path.Combine(options.DataDirectory, LogDirectoryName), sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService<PerformanceRecording>();

        services.AddBackbloomServer();

        // Automatic updates (installed copies only; see packaging/README.md).
        services.AddSingleton(UpdateSettings.FromConfiguration(builder.Configuration));
        services.AddSingleton<IUpdateClient, VelopackUpdateClient>();
        services.AddHostedService<UpdateService>();
        configure?.Invoke(options, services);

        var app = builder.Build();
        level.Follow(app.Services.GetRequiredService<ISettingsStore>());
        app.UseBackbloomServer();
        return app;
    }

    /// <summary>
    /// <c>backbloom --import &lt;database&gt; [--data-dir &lt;dir&gt;]</c>: imports a HyperHDR database into
    /// the data directory (replacing its settings and credentials, which are saved to backups/ first) and exits.
    /// </summary>
    /// <returns>The process exit code.</returns>
    public static int Import(string source, string[] args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables("BACKBLOOM_").AddCommandLine(args).Build();
        var options = AppOptions.FromConfiguration(configuration);
        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(ConfigureConsole));
        var logger = loggerFactory.CreateLogger("Backbloom");
        try
        {
            using var settings = SettingsServices.Open(new SettingsOptions(options.DataDirectory, null), CreateSchema(new DriverRegistry().RegisterAll()), loggerFactory);
            var outcome = settings.ImportDatabase(source);
            if (outcome.BackupPath is { } backup)
            {
                StartupLog.PreviousSettingsSaved(logger, backup);
            }

            return outcome.Stored.Success ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StartupLog.ImportFailed(logger, ex, source);
            return 1;
        }
    }

    private static void ConfigureConsole(Microsoft.Extensions.Logging.Console.SimpleConsoleFormatterOptions console)
    {
        console.SingleLine = true;
        console.TimestampFormat = "HH:mm:ss.fff ";
    }

    private static SettingsSchema CreateSchema(DriverRegistry drivers) =>
        SettingsSchema.CreateBuiltin(drivers.Drivers.ToDictionary(d => d.Type, d => d.Schema, StringComparer.Ordinal));

    /// <summary>The product version and build id (the first 8 characters of the commit) of this build.</summary>
    public static (string Version, string Build) ProductVersion()
    {
        var informational = typeof(BackbloomHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0
            ? (informational, "dev")
            : (informational[..plus], informational[(plus + 1)..Math.Min(informational.Length, plus + 1 + 8)]);
    }

    private static ServerOptions CreateServerOptions(AppOptions options, SettingsDatabase database)
    {
        var (version, build) = ProductVersion();
        return new ServerOptions
        {
            UiDirectory = options.UiDirectory,
            Version = version,
            Build = build,
            BuildTime = File.GetLastWriteTimeUtc(typeof(BackbloomHost).Assembly.Location).ToString("u", CultureInfo.InvariantCulture),
            InstanceId = database.GetOrCreateInstanceId(),
        };
    }
}
