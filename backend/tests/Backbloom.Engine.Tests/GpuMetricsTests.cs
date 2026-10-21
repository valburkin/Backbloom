using Backbloom.Platform.Windows;

namespace Backbloom.Engine.Tests;

/// <summary>The GPU load from the Windows performance counters (Task Manager's numbers).</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[Collection(SerialTests.Name)]
public sealed class GpuMetricsTests
{
    [Fact]
    public void Utilization_IsTheBusiestEngine_SummedOverProcesses_ForTheComputerAndForThisProcess()
    {
        (string, double)[] engines =
        [
            ("pid_100_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 30),
            ("pid_200_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 25),
            ("pid_200_luid_0x0_0x1_phys_0_eng_3_engtype_Copy", 4),
            ("pid_100_luid_0x0_0x1_phys_0_eng_3_engtype_Copy", 1.5),
            ("pid_100_luid_0x0_0x2_phys_0_eng_0_engtype_3D", 2),
            ("not an engine", 99),
        ];

        // the 3D engine of adapter 1 is the busiest: 55 % (30 + 25); this process's busiest is its share of it, 25 %
        Assert.Equal((55.0, 25.0), WindowsGpuMetrics.Utilization(engines, "pid_200_"));
        Assert.Equal((55.0, 0.0), WindowsGpuMetrics.Utilization(engines, "pid_300_"));
        Assert.Equal((null, null), WindowsGpuMetrics.Utilization([], "pid_200_"));
    }

    [Fact]
    public void Sample_ReadsTheCounters_WhenTheMachineHasThem()
    {
        using var gpu = new WindowsGpuMetrics();

        // the utilization is a rate: known from the second reading
        var first = gpu.Sample();
        Assert.Null(first.ComputerPercent);
        Thread.Sleep(200);
        var second = gpu.Sample();
        Assert.SkipWhen(second.ComputerPercent is null, "no GPU engine counters on this machine");
        Assert.InRange(second.ComputerPercent!.Value, 0, 100);
        Assert.InRange(second.AppPercent!.Value, 0, 100);
        Assert.True(second.ComputerDedicatedBytes >= 0);
        Assert.True(second.AppSharedBytes is null or >= 0);
    }
}
