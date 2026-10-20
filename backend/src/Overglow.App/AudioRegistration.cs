using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Overglow.Audio;
using Overglow.Audio.Windows;

namespace Overglow.App;

/// <summary>Registers music mode's sound capture: WASAPI on Windows, none elsewhere (music effects stay dark).</summary>
internal static class AudioRegistration
{
    public static IServiceCollection AddAudio(this IServiceCollection services)
    {
        services.AddSingleton(sp => new SoundCapture(
            OperatingSystem.IsWindows() ? CreateWasapi(sp) : null,
            sp.GetRequiredService<ILogger<SoundCapture>>()));
        return services;
    }

    [SupportedOSPlatform("windows")]
    private static WasapiAudioCapture CreateWasapi(IServiceProvider services) =>
        new(services.GetRequiredService<ILogger<WasapiAudioCapture>>());
}
