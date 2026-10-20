using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Backbloom.Settings;
using Backbloom.Tests.Platform;

namespace Backbloom.Engine.Tests;

/// <summary>
/// The first-run hold and HyperHDR coexistence with a fake process list and a fake clock (HyperHDR really runs on the
/// development machine, so nothing here looks at the real process list).
/// </summary>
public sealed class LightingGateTests
{
    private readonly FakeProcessList _processes = new();
    private readonly FakeTimeProvider _time = new();

    private OtherAppMonitor Monitor() => new(_processes, _time, NullLogger<OtherAppMonitor>.Instance);

    [Fact]
    public void Monitor_FindsHyperHdrAtStart_ThenPollsEveryTwoSeconds()
    {
        _processes.Start("HyperHDR.exe");
        using var monitor = Monitor();
        var changes = new List<OtherLightingApp?>();
        monitor.Changed += changes.Add;

        monitor.Start();
        Assert.Equal("HyperHDR", monitor.Running?.Name);
        Assert.Equal(1, _processes.Calls);

        _processes.Stop("HyperHDR.exe");
        _time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.NotNull(monitor.Running);
        _time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Null(monitor.Running);

        _processes.Start("hyperiond.exe");
        _time.Advance(OtherAppMonitor.PollInterval);
        Assert.Equal("Hyperion", monitor.Running?.Name);
        Assert.Equal(["HyperHDR", null, "Hyperion"], changes.Select(c => c?.Name));
        Assert.Equal(3, _processes.Calls);
    }

    [Fact]
    public void Monitor_KeepsItsStateWhenTheProcessListFails()
    {
        var failing = new ThrowingProcessList();
        using var monitor = new OtherAppMonitor(failing, _time, NullLogger<OtherAppMonitor>.Instance);
        monitor.Start();
        _time.Advance(OtherAppMonitor.PollInterval * 3);
        Assert.Null(monitor.Running);
        Assert.Equal(4, failing.Calls);
    }

    [Fact]
    public void FirstRun_HoldsTheOutput_UntilCompleted_ThenRemembersIt()
    {
        var components = new ComponentStates();
        var setup = new MemorySetupState();
        using var gate = new LightingGate(components, setup, Monitor(), NullLogger<LightingGate>.Instance);
        var changes = new List<LightingGateStatus>();
        gate.Changed += changes.Add;

        gate.Start();
        Assert.True(components.OutputHeld);
        Assert.False(components.OutputActive);
        Assert.True(components.IsOn(Component.LedDevice));
        Assert.True(gate.Status.OutputHeld);

        var status = gate.CompleteFirstRun();
        Assert.Equal(new LightingGateStatus(true, null), status);
        Assert.False(components.OutputHeld);
        Assert.True(components.OutputActive);
        Assert.True(setup.FirstRunCompleted);
        Assert.Equal([new LightingGateStatus(true, null)], changes);
    }

    [Fact]
    public void HyperHdr_HoldsTheOutput_WhileItRuns()
    {
        var components = new ComponentStates();
        var setup = new MemorySetupState { FirstRunCompleted = true };
        _processes.Start("hyperhdr.exe");
        using var gate = new LightingGate(components, setup, Monitor(), NullLogger<LightingGate>.Instance);
        var held = new List<bool>();
        components.HoldChanged += held.Add;

        gate.Start();
        Assert.True(components.OutputHeld);
        Assert.Equal("HyperHDR", gate.Status.OtherApp?.Name);

        // starting the lighting doesn't override HyperHDR
        Assert.True(gate.CompleteFirstRun().OutputHeld);

        _processes.Stop("hyperhdr.exe");
        _time.Advance(OtherAppMonitor.PollInterval);
        Assert.False(components.OutputHeld);
        Assert.Null(gate.Status.OtherApp);

        // HyperHDR started again later: Backbloom lets go of the LEDs again
        _processes.Start("hyperhdr.exe");
        _time.Advance(OtherAppMonitor.PollInterval);
        Assert.True(components.OutputHeld);
        Assert.Equal([true, false, true], held);
    }

    [Fact]
    public void ConfiguredSetup_WithoutHyperHdr_IsNotHeld()
    {
        var components = new ComponentStates();
        using var gate = new LightingGate(components, new MemorySetupState { FirstRunCompleted = true }, Monitor(), NullLogger<LightingGate>.Instance);
        gate.Start();
        Assert.False(components.OutputHeld);
        Assert.False(gate.Status.OutputHeld);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void WindowsProcessList_FindsThisProcessByImageName()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var self = Path.GetFileName(Environment.ProcessPath!);
        var list = new Backbloom.Platform.Windows.WindowsProcessList();

        Assert.Equal(self.ToUpperInvariant(), list.FindRunning(["not-running-backbloom-test.exe", self.ToUpperInvariant()]));
        Assert.Null(list.FindRunning(["not-running-backbloom-test.exe"]));
        Assert.Equal(self, new Backbloom.Engine.Platform.PortableProcessList().FindRunning([self]));
    }

    private sealed class ThrowingProcessList : Backbloom.Engine.Platform.IProcessList
    {
        public int Calls { get; private set; }

        public string? FindRunning(IReadOnlyList<string> executableNames)
        {
            Calls++;
            throw new InvalidOperationException("no snapshot");
        }
    }
}

/// <summary>An <see cref="ISetupState"/> in memory.</summary>
internal sealed class MemorySetupState : ISetupState
{
    private readonly HashSet<string> _acknowledged = [];

    public bool FirstRunCompleted { get; set; }

    public string? ImportedFrom { get; set; }

    public void CompleteFirstRun() => FirstRunCompleted = true;

    public bool IsAcknowledged(string topic) => _acknowledged.Contains(topic);

    public void Acknowledge(string topic) => _acknowledged.Add(topic);
}
