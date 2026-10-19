using System.Net;
using System.Text.Json.Nodes;

namespace Overglow.Server.Auth;

/// <summary>The <c>network</c> settings that decide what a new connection may do without signing in.</summary>
public sealed record AccessPolicy(bool ApiAuth = true, bool LocalApiAuth = false, bool LocalAdminAuth = true)
{
    public static AccessPolicy FromSettings(JsonNode? network)
    {
        var o = network as JsonObject;
        return new AccessPolicy(Read(o, "apiAuth", true), Read(o, "localApiAuth", false), Read(o, "localAdminAuth", true));

        static bool Read(JsonObject? o, string name, bool fallback) =>
            o?[name] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;
    }

    /// <summary>
    /// Initial rights of a connection (port of <c>BaseAPI::init</c>): local clients get API access unless
    /// <c>localApiAuth</c> is set and admin access when <c>localAdminAuth</c> is off.
    /// </summary>
    public (bool Authorized, bool Admin) InitialRights(bool isLocal)
    {
        var authorized = !ApiAuth || (isLocal && !LocalApiAuth);
        var admin = isLocal && !LocalAdminAuth;
        return (authorized || admin, admin);
    }
}

/// <summary>Whether a peer is on the local network (port of <c>NetOrigin::isLocalAddress</c>).</summary>
public static class NetworkOrigin
{
    /// <summary>
    /// True when <paramref name="remote"/> is in the same /24 (IPv4) or /64 (IPv6) network as the address the connection
    /// arrived on, which includes loopback connections.
    /// </summary>
    public static bool IsLocal(IPAddress? remote, IPAddress? local)
    {
        if (remote is null)
        {
            return false;
        }

        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }

        if (local?.IsIPv4MappedToIPv6 == true)
        {
            local = local.MapToIPv4();
        }

        if (IPAddress.IsLoopback(remote))
        {
            return true;
        }

        if (local is null || local.AddressFamily != remote.AddressFamily)
        {
            return false;
        }

        // /24 and /64 are whole bytes: compare the leading 3 (IPv4) or 8 (IPv6) bytes.
        var prefixBytes = remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 3 : 8;
        Span<byte> a = stackalloc byte[16];
        Span<byte> b = stackalloc byte[16];
        remote.TryWriteBytes(a, out _);
        local.TryWriteBytes(b, out _);
        return a[..prefixBytes].SequenceEqual(b[..prefixBytes]);
    }
}
