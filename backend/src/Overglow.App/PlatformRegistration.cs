using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Overglow.Engine.Platform;
using Overglow.Platform.Windows;

namespace Overglow.App;

/// <summary>Registers the platform's clock, system metrics and process list (portable fallbacks elsewhere).</summary>
internal static class PlatformRegistration
{
    public static IServiceCollection AddPlatform(this IServiceCollection services)
    {
        if (OperatingSystem.IsWindows())
        {
            AddWindows(services);
        }
        else
        {
            services.AddSingleton<IClock, SystemClock>();
            services.AddSingleton<ISystemMetrics, ProcessMetrics>();
            services.AddSingleton<IProcessList, PortableProcessList>();
        }

        return services;
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindows(IServiceCollection services)
    {
        services.AddSingleton<IClock, WindowsClock>();
        services.AddSingleton<ISystemMetrics, WindowsSystemMetrics>();
        services.AddSingleton<IProcessList, WindowsProcessList>();
    }
}
