namespace Backbloom.Server;

/// <summary>Whether the web UI can be opened from other devices, and at which addresses.</summary>
/// <param name="Enabled">The server listens on the network (not only on 127.0.0.1).</param>
/// <param name="FixedByCommandLine"><c>--bind</c> decides where the server listens; the setting has no effect.</param>
/// <param name="Port">The web port.</param>
/// <param name="Urls">Addresses phones on the same network can open (empty while <paramref name="Enabled"/> is false).</param>
/// <param name="Advertised">The UI is announced with mDNS (<c>_backbloom-http._tcp</c>).</param>
public sealed record NetworkAccessStatus(bool Enabled, bool FixedByCommandLine, int Port, IReadOnlyList<string> Urls, bool Advertised);

/// <summary>Where the web server listens; implemented by the host, which owns the listeners.</summary>
public interface INetworkAccess
{
    NetworkAccessStatus Status { get; }

    /// <summary>Raised after <see cref="Status"/> changed (the setting, or the computer's addresses).</summary>
    event Action? Changed;
}
