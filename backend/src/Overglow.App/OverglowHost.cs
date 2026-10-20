using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public static WebApplication Build(string[] args) => Build(args, null);

    /// <param name="args">Command line (see <see cref="AppOptions"/>).</param>
    /// <param name="configure">Lets tests replace services (e.g. the frame source) before the host is built.</param>
    public static WebApplication Build(string[] args, Action<AppOptions, IServiceCollection>? configure)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.AddEnvironmentVariables("OVERGLOW_");
        var options = AppOptions.FromConfiguration(builder.Configuration);
        Directory.CreateDirectory(options.DataDirectory);

        builder.WebHost.UseKestrel(k => k.Listen(options.BindAddress, options.Port));

        var logs = new LogBuffer();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(ConfigureConsole);
        builder.Logging.AddProvider(new LogBufferProvider(logs));
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton(logs);

        // LED drivers and what they share.
        services.AddSingleton(_ => new DriverRegistry().RegisterAll());
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }));
        services.AddSingleton(sp => new DriverContext(sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<HttpClient>()));

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
        services.AddSingleton<DeviceManager>();
        services.AddSingleton<LightingEngine>();
        services.AddHostedService<EngineHostedService>();

        services.AddOverglowServer();
        configure?.Invoke(options, services);

        var app = builder.Build();
        app.UseOverglowServer();
        return app;
    }

    /// <summary>
    /// <c>overglow import &lt;database&gt; [--data-dir &lt;dir&gt;]</c>: imports a HyperHDR or Overglow C++ database into
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

    private static ServerOptions CreateServerOptions(AppOptions options, SettingsDatabase database)
    {
        var assembly = typeof(OverglowHost).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return new ServerOptions
        {
            UiDirectory = options.UiDirectory,
            Version = plus < 0 ? informational : informational[..plus],
            Build = plus < 0 ? "dev" : informational[(plus + 1)..Math.Min(informational.Length, plus + 1 + 8)],
            BuildTime = File.GetLastWriteTimeUtc(assembly.Location).ToString("u", CultureInfo.InvariantCulture),
            InstanceId = database.GetOrCreateInstanceId(),
        };
    }
}
