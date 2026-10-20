namespace Overglow.Engine.Platform;

/// <summary>
/// Starting the app when the user signs in (Windows: the per-user Run key). Off unless the user turns it on (tray
/// menu, the first-run welcome); nothing enables it on its own.
/// </summary>
public interface IAutostart
{
    /// <summary>Menu label, e.g. "Start with Windows".</summary>
    string Label { get; }

    /// <summary>True when the entry exists and starts this executable.</summary>
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}
