using System.Net;

namespace Overglow.App;

/// <summary>How the process presents itself (from <c>--service</c> / <c>--no-window</c>).</summary>
/// <param name="Desktop">Tray icon and window (Windows; not with <c>--service</c>).</param>
/// <param name="OpenWindow">Open the window once the server runs (not with <c>--no-window</c>).</param>
/// <param name="ConsoleLogging">Log to the console as well (when the process has one).</param>
public sealed record LaunchOptions(bool Desktop, bool OpenWindow, bool ConsoleLogging)
{
    /// <summary>No tray, no window, console logging: tests and <c>--service</c>.</summary>
    public static LaunchOptions Headless { get; } = new(false, false, true);
}

/// <summary>Addresses derived from the options.</summary>
public static class AppUrls
{
    /// <summary>
    /// The web UI as this computer reaches it: loopback when the server listens on all addresses (127.0.0.1 rather
    /// than "localhost", which may resolve to ::1 first while the server listens on IPv4 only).
    /// </summary>
    public static Uri Ui(AppOptions options, int? port = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var address = options.BindAddress;
        var host = address.Equals(IPAddress.Any) || IPAddress.IsLoopback(address) ? "127.0.0.1"
            : address.Equals(IPAddress.IPv6Any) ? "[::1]"
            : address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]"
            : address.ToString();
        return new Uri($"http://{host}:{port ?? options.Port}/");
    }
}
