using Microsoft.Extensions.Logging;
using Overglow.Settings;

namespace Overglow.Engine;

/// <summary>Why the LEDs may not light up yet.</summary>
/// <param name="FirstRunCompleted">The user started the lighting once (the first-run welcome).</param>
/// <param name="OtherApp">Another lighting app that drives the LEDs now (HyperHDR), or null.</param>
public sealed record LightingGateStatus(bool FirstRunCompleted, OtherLightingApp? OtherApp)
{
    /// <summary>Nothing is sent to the controllers and the startup effect waits.</summary>
    public bool OutputHeld => !FirstRunCompleted || OtherApp is not null;
}

/// <summary>
/// Decides whether Overglow may drive the LED controllers: not before the user pressed "Start lighting" in the
/// first-run welcome (a fresh install or import must not take over the lights by itself), and never while
/// HyperHDR runs (two senders make the LEDs flicker). Holds the output through
/// <see cref="ComponentStates.SetHeld"/>; capture and the preview keep running, the component switches keep the
/// user's choice. <see cref="Start"/> runs before the engine starts, so a held start never opens a controller.
/// </summary>
public sealed partial class LightingGate : IDisposable
{
    private readonly ComponentStates _components;
    private readonly ISetupState _setup;
    private readonly OtherAppMonitor _monitor;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private LightingGateStatus _status;
    private bool _started;

    public LightingGate(ComponentStates components, ISetupState setup, OtherAppMonitor monitor, ILogger<LightingGate> logger)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(logger);
        _components = components;
        _setup = setup;
        _monitor = monitor;
        _logger = logger;
        _status = new LightingGateStatus(setup.FirstRunCompleted, null);
    }

    /// <summary>Raised after <see cref="Status"/> changed (on the thread that changed it).</summary>
    public event Action<LightingGateStatus>? Changed;

    public LightingGateStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>Checks for HyperHDR, applies the hold and keeps following both.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _monitor.Changed += OnOtherAppChanged;
        _monitor.Start();
        Apply();
        if (!_setup.FirstRunCompleted)
        {
            LogWaitingForFirstRun();
        }
    }

    /// <summary>"Start lighting": remembers it, then the output follows (still held while HyperHDR runs).</summary>
    public LightingGateStatus CompleteFirstRun()
    {
        if (!_setup.FirstRunCompleted)
        {
            _setup.CompleteFirstRun();
            LogFirstRunCompleted();
        }

        return Apply();
    }

    public void Dispose()
    {
        _monitor.Changed -= OnOtherAppChanged;
        _monitor.Dispose();
    }

    private void OnOtherAppChanged(OtherLightingApp? app) => Apply();

    private LightingGateStatus Apply()
    {
        LightingGateStatus status;
        bool changed;
        lock (_lock)
        {
            status = new LightingGateStatus(_setup.FirstRunCompleted, _monitor.Running);
            changed = status != _status;
            _status = status;
            _components.SetHeld(status.OutputHeld);
        }

        if (changed)
        {
            Changed?.Invoke(status);
        }

        return status;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "First start: the LEDs stay off until the lighting is started in the welcome window")]
    private partial void LogWaitingForFirstRun();

    [LoggerMessage(Level = LogLevel.Information, Message = "The lighting was started for the first time")]
    private partial void LogFirstRunCompleted();
}
