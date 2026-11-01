using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Backbloom.Capture;
using Backbloom.Capture.Hdr;
using Backbloom.Capture.Windows;
using Backbloom.Settings;

namespace Backbloom.App;

/// <summary>
/// Registers the frame source chosen by <c>--capture</c>: screen capture configured by the <c>systemGrabber</c>
/// settings (reconfigured on every change of that section, and with Game mode's rate while it is on, see
/// <see cref="CaptureModeFollower"/>), the synthetic test pattern, or a still picture (<c>--capture-image</c>).
/// </summary>
internal static partial class CaptureRegistration
{
    public static IServiceCollection AddCapture(this IServiceCollection services, AppOptions options)
    {
        services.AddSingleton(sp => new LutProvider(
            new LutLocations(options.LutDirectories, options.LutDirectories.Count > 0 ? options.LutDirectories[0] : options.DataDirectory),
            sp.GetRequiredService<ILogger<LutProvider>>()));
        services.AddSingleton<CaptureModeFollower>();
        services.AddSingleton(sp => CreateSource(options, sp));
        return services;
    }

    private static IFrameSource CreateSource(AppOptions options, IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CaptureRegistration));
        if (options is { Capture: CaptureKind.Image, CaptureImage: { } image })
        {
            return new ImageFrameSource(image, options.SyntheticFps);
        }

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
        var follower = services.GetRequiredService<CaptureModeFollower>();
        var source = new DxgiScreenSource(
            follower.SettingsFrom(store),
            services.GetRequiredService<LutProvider>(),
            services.GetRequiredService<ILogger<DxgiScreenSource>>());
        follower.Follow(store, source.Configure);
        return source;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen capture is not available on {Os}; using the synthetic test pattern")]
    private static partial void LogNoScreenCapture(ILogger logger, string os);
}
