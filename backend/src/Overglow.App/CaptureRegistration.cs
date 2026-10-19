using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Overglow.Capture;
using Overglow.Capture.Hdr;
using Overglow.Capture.Windows;
using Overglow.Settings;

namespace Overglow.App;

/// <summary>
/// Registers the frame source chosen by <c>--capture</c>: screen capture configured by the <c>systemGrabber</c>
/// settings (reconfigured on every change of that section), or the synthetic test pattern.
/// </summary>
internal static partial class CaptureRegistration
{
    public static IServiceCollection AddCapture(this IServiceCollection services, AppOptions options)
    {
        services.AddSingleton(sp => new LutProvider(
            new LutLocations(options.LutDirectories, options.LutDirectories.Count > 0 ? options.LutDirectories[0] : options.DataDirectory),
            sp.GetRequiredService<ILogger<LutProvider>>()));
        services.AddSingleton(sp => CreateSource(options, sp));
        return services;
    }

    private static IFrameSource CreateSource(AppOptions options, IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CaptureRegistration));
        if (options.Capture == CaptureKind.Screen)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            {
                return CreateScreenSource(services);
            }

            LogNoScreenCapture(logger, Environment.OSVersion.VersionString);
        }

        return new SyntheticFrameSource(new SyntheticFrameSourceOptions(FramesPerSecond: options.SyntheticFps));
    }

    [SupportedOSPlatform("windows10.0.17763")]
    private static DxgiScreenSource CreateScreenSource(IServiceProvider services)
    {
        var store = services.GetRequiredService<ISettingsStore>();
        var source = new DxgiScreenSource(
            CaptureSettings.FromJson(store.GetSection(CaptureSettings.SectionName)),
            services.GetRequiredService<LutProvider>(),
            services.GetRequiredService<ILogger<DxgiScreenSource>>());
        store.Changed += change =>
        {
            if (change.Sections.TryGetValue(CaptureSettings.SectionName, out var section))
            {
                source.Configure(CaptureSettings.FromJson(section));
            }
        };
        return source;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen capture is not available on {Os}; using the synthetic test pattern")]
    private static partial void LogNoScreenCapture(ILogger logger, string os);
}
