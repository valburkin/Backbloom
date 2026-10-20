namespace Backbloom.Engine;

/// <summary>Switchable parts of the lighting, as named in the JSON-RPC protocol (<c>componentstate</c>).</summary>
public enum Component
{
    /// <summary>"ALL": the whole lighting; off means LEDs off and no processing.</summary>
    All,

    /// <summary>"SYSTEMGRABBER": the screen (frame source).</summary>
    SystemGrabber,

    /// <summary>"LEDDEVICE": output to the controllers.</summary>
    LedDevice,

    /// <summary>"SMOOTHING": smoothing of the LED colors (initially the <c>smoothing.enable</c> setting).</summary>
    Smoothing,

    /// <summary>"BLACKBORDER": black border detection (initially the <c>blackborderdetector.enable</c> setting).</summary>
    BlackBorder,
}

public static class Components
{
    /// <summary>
    /// Every switchable component in the order <c>serverinfo.components</c> lists them (HyperHDR's; effects had no
    /// switch there either). The <c>componentstate</c> command handles every member.
    /// </summary>
    public static IReadOnlyList<Component> All { get; } = [Component.All, Component.Smoothing, Component.BlackBorder, Component.SystemGrabber, Component.LedDevice];

    public static string ToId(this Component component) => component switch
    {
        Component.All => "ALL",
        Component.SystemGrabber => "SYSTEMGRABBER",
        Component.LedDevice => "LEDDEVICE",
        Component.Smoothing => "SMOOTHING",
        Component.BlackBorder => "BLACKBORDER",
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    public static bool TryParse(string? id, out Component component)
    {
        switch (id?.ToUpperInvariant())
        {
            case "ALL":
                component = Component.All;
                return true;
            case "SYSTEMGRABBER":
            case "GRABBER":
                component = Component.SystemGrabber;
                return true;
            case "LEDDEVICE":
                component = Component.LedDevice;
                return true;
            case "SMOOTHING":
                component = Component.Smoothing;
                return true;
            case "BLACKBORDER":
                component = Component.BlackBorder;
                return true;
            default:
                component = default;
                return false;
        }
    }
}

/// <summary>On/off state of each <see cref="Component"/>; thread-safe.</summary>
public sealed class ComponentStates
{
    private readonly bool[] _states;
    private bool _held;

    public ComponentStates(bool ledDeviceOn = true, bool smoothingOn = false, bool blackBorderOn = false)
    {
        _states = new bool[Components.All.Count];
        _states[(int)Component.All] = true;
        _states[(int)Component.SystemGrabber] = true;
        _states[(int)Component.LedDevice] = ledDeviceOn;
        _states[(int)Component.Smoothing] = smoothingOn;
        _states[(int)Component.BlackBorder] = blackBorderOn;
    }

    /// <summary>Raised after a component changed (on the thread that changed it).</summary>
    public event Action<Component, bool>? Changed;

    /// <summary>Raised after <see cref="OutputHeld"/> changed (on the thread that changed it).</summary>
    public event Action<bool>? HoldChanged;

    /// <summary>
    /// True while the controllers must stay off whatever the switches say: the first-run welcome wasn't completed
    /// yet, or another lighting app drives the LEDs (see <see cref="LightingGate"/>). Not a protocol component.
    /// </summary>
    public bool OutputHeld => Volatile.Read(ref _held);

    public bool IsOn(Component component) => Volatile.Read(ref _states[(int)component]);

    /// <summary>True when the frame source should run.</summary>
    public bool CaptureActive => IsOn(Component.All) && IsOn(Component.SystemGrabber);

    /// <summary>True when the controllers should be on.</summary>
    public bool OutputActive => IsOn(Component.All) && IsOn(Component.LedDevice) && !OutputHeld;

    /// <summary>Sets a component; returns false when it already had that state.</summary>
    public bool Set(Component component, bool on)
    {
        if (Interlocked.Exchange(ref _states[(int)component], on) == on)
        {
            return false;
        }

        Changed?.Invoke(component, on);
        return true;
    }

    /// <summary>Holds or releases the output; returns false when it already had that state.</summary>
    public bool SetHeld(bool held)
    {
        if (Interlocked.Exchange(ref _held, held) == held)
        {
            return false;
        }

        HoldChanged?.Invoke(held);
        return true;
    }
}
