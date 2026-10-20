using System.Diagnostics;

namespace Backbloom.Shell;

/// <summary>One entry of the tray menu; built anew each time the menu opens.</summary>
/// <param name="Text">Label; <see cref="Separator"/> draws a line.</param>
/// <param name="Invoke">Runs on the platform's UI thread when the entry is chosen; null for a submenu or a label.</param>
/// <param name="IsDefault">Shown bold; the same action runs when the tray icon is clicked.</param>
public sealed record TrayMenuItem(
    string Text,
    Action? Invoke = null,
    bool Checked = false,
    bool Enabled = true,
    bool IsDefault = false,
    IReadOnlyList<TrayMenuItem>? Children = null)
{
    public static TrayMenuItem Separator { get; } = new("-");

    public bool IsSeparator => ReferenceEquals(this, Separator);
}

/// <summary>The notification area icon (Windows: <c>Shell_NotifyIcon</c>).</summary>
public interface ITrayIcon : IDisposable
{
    /// <summary>
    /// Adds the icon. <paramref name="menu"/> builds the context menu each time it opens; <paramref name="activated"/>
    /// runs on a click of the icon. Both run on the platform's UI thread.
    /// </summary>
    void Show(string tooltip, Func<IReadOnlyList<TrayMenuItem>> menu, Action activated);
}

/// <summary>The native window that shows the web UI.</summary>
public interface IAppWindow : IDisposable
{
    /// <summary>
    /// Opens the window (or brings it to the front). Without a web view runtime the UI opens in the default browser
    /// instead. May be called from any thread.
    /// </summary>
    void Show();
}

/// <summary>Opens a page in the user's default browser.</summary>
public interface IBrowserLauncher
{
    void Open(Uri url);
}

/// <summary>Uses the OS's URL handler (<c>ShellExecute</c>, <c>open</c>, <c>xdg-open</c>).</summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public void Open(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Only web pages are opened.", nameof(url));
        }

        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            start = new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true };
        }
        else
        {
            start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open");
            start.ArgumentList.Add(url.AbsoluteUri);
        }

        using var process = Process.Start(start);
    }
}
