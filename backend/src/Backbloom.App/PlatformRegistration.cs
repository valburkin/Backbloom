using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Backbloom.Devices.Setup;
using Backbloom.Engine.Platform;
using Backbloom.Platform.Windows;

namespace Backbloom.App;

/// <summary>Registers the platform's clock, system and GPU metrics, process list, program list, monitor list and Wi-Fi (portable fallbacks elsewhere).</summary>
internal static class PlatformRegistration
{
    /// <summary>
    /// Key of the system metrics the performance recording samples: its own instance, since a sample measures the time
    /// since the previous one, and the UI's tiles sample on their own schedule.
    /// </summary>
    public const string RecorderMetrics = "recorder";

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
            services.AddKeyedSingleton<ISystemMetrics, ProcessMetrics>(RecorderMetrics);
            services.AddSingleton<IGpuMetrics, NoGpuMetrics>();
            services.AddSingleton<IProcessList, PortableProcessList>();
            services.AddSingleton<IProgramList, PortableProgramList>();
            services.AddSingleton<IDisplayList, NoDisplayList>();
            services.AddSingleton<IWifi, NoWifi>();
        }

        return services;
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindows(IServiceCollection services)
    {
        services.AddSingleton<IClock, WindowsClock>();
        services.AddSingleton<ISystemMetrics, WindowsSystemMetrics>();
        services.AddKeyedSingleton<ISystemMetrics, WindowsSystemMetrics>(RecorderMetrics);
        services.AddSingleton<IGpuMetrics, WindowsGpuMetrics>();
        services.AddSingleton<IProcessList, WindowsProcessList>();
        services.AddSingleton<IProgramList, WindowsProgramList>();
        services.AddSingleton<IDisplayList, WindowsDisplayList>();
        services.AddSingleton<IWifi, WindowsWifi>();
    }
}
