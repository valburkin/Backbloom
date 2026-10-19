using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Overglow.Capture;
using Overglow.Devices;
using Overglow.Engine;
using Overglow.Server;
using Overglow.Server.Logging;
using Overglow.Settings;
using Overglow.Settings.Auth;

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
        builder.Logging.AddSimpleConsole(c =>
        {
            c.SingleLine = true;
            c.TimestampFormat = "HH:mm:ss.fff ";
        });
        builder.Logging.AddProvider(new LogBufferProvider(logs));
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton(logs);
        services.AddSingleton(CreateServerOptions(options));

        // LED drivers and what they share.
        services.AddSingleton(_ => new DriverRegistry().RegisterAll());
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }));
        services.AddSingleton(sp => new DriverContext(sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<HttpClient>()));

        // Settings and credentials in the data directory.
        services.AddSingleton(sp => SettingsSchema.CreateBuiltin(
            sp.GetRequiredService<DriverRegistry>().Drivers.ToDictionary(d => d.Type, d => d.Schema, StringComparer.Ordinal)));
        services.AddSingleton<ISettingsStore>(sp => new JsonFileSettingsStore(
            options.DataDirectory, sp.GetRequiredService<SettingsSchema>(), sp.GetRequiredService<ILogger<JsonFileSettingsStore>>()));
        services.AddSingleton<IAuthStore>(_ => new JsonFileAuthStore(options.DataDirectory));

        // The lighting: frame source -> pipeline -> controllers.
        services.AddSingleton<IFrameSource>(_ => new SyntheticFrameSource(new SyntheticFrameSourceOptions(FramesPerSecond: options.SyntheticFps)));
        services.AddSingleton<DeviceManager>();
        services.AddSingleton<LightingEngine>();
        services.AddHostedService<EngineHostedService>();

        services.AddOverglowServer();
        configure?.Invoke(options, services);

        var app = builder.Build();
        app.UseOverglowServer();
        return app;
    }

    private static ServerOptions CreateServerOptions(AppOptions options)
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
            InstanceId = InstanceId.LoadOrCreate(options.DataDirectory),
        };
    }
}
