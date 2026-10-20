using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Overglow.App.Logging;
using Overglow.Devices;
using Overglow.Engine;
using Overglow.Server;
using Overglow.Server.Logging;
using Overglow.Settings;
using Overglow.Settings.Auth;
using Overglow.Settings.Storage;

namespace Overglow.App;

/// <summary>
/// The composition root: every service is created here and handed to its users through constructor injection
/// (no static singletons or service locators). Integration tests build the same host on a random port.
/// </summary>
public static class OverglowHost
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
        builder.Configuration.AddEnvironmentVariables("OVERGLOW_");
        var options = AppOptions.FromConfiguration(builder.Configuration);
        Directory.CreateDirectory(options.DataDirectory);

        builder.WebHost.UseKestrel(k => k.Listen(options.BindAddress, options.Port));

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
        services.AddSingleton(sp => CreateServerOptions(options, sp.GetRequiredService<SettingsServices>().Database));

        // The lighting: frame source -> pipeline -> controllers.
        services.AddPlatform();
        services.AddCapture(options);
        services.AddAudio();
        services.AddSingleton<DeviceManager>();
        services.AddSingleton<LightingEngine>();

        // Registered before the engine so that it stops after it: the tray icon goes once the LEDs are off.
        services.AddDesktop(options, launch);
        services.AddHostedService<EngineHostedService>();

        services.AddOverglowServer();
        configure?.Invoke(options, services);

        var app = builder.Build();
        level.Follow(app.Services.GetRequiredService<ISettingsStore>());
        app.UseOverglowServer();
        return app;
    }

    /// <summary>
    /// <c>overglow --import &lt;database&gt; [--data-dir &lt;dir&gt;]</c>: imports a HyperHDR or Overglow C++ database into
    /// the data directory (replacing its settings and credentials, which are saved to backups/ first) and exits.
    /// </summary>
    /// <returns>The process exit code.</returns>
    public static int Import(string source, string[] args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables("OVERGLOW_").AddCommandLine(args).Build();
        var options = AppOptions.FromConfiguration(configuration);
        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(ConfigureConsole));
        var logger = loggerFactory.CreateLogger("Overglow");
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
        var informational = typeof(OverglowHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0";
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
            BuildTime = File.GetLastWriteTimeUtc(typeof(OverglowHost).Assembly.Location).ToString("u", CultureInfo.InvariantCulture),
            InstanceId = database.GetOrCreateInstanceId(),
        };
    }
}
