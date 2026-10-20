using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Backbloom.Server.Auth;

/// <summary>Where a request comes from, as far as access is concerned.</summary>
public enum ClientOrigin
{
    /// <summary>A program on this computer (the app's window, a browser, a script): full access, no password.</summary>
    ThisComputer,

    /// <summary>Another device (only reachable with remote access on): needs the password or a token.</summary>
    Network,

    /// <summary>
    /// A loopback connection that names a host or origin other than this computer: a website open in a browser on
    /// this computer trying to reach the API (DNS rebinding, cross-site requests). Refused.
    /// </summary>
    Rejected,
}

/// <summary>
/// The access rule: this computer always has full access, other devices always sign in (HyperHDR's
/// <c>localAdminAuth</c>, <c>localApiAuth</c> and <c>apiAuth</c> options are gone). "This computer" is decided by the
/// connection's peer address only (127.0.0.0/8, ::1, IPv4-mapped 127.x), never by headers such as
/// <c>X-Forwarded-For</c>, and never by this computer's own network addresses (a browser here that opens the LAN
/// address is treated like another device). Because any website open in a local browser can make requests to
/// 127.0.0.1, a loopback request must also name a loopback host (<c>Host</c>) and, when a browser sends one, a loopback
/// <c>Origin</c>.
/// </summary>
public static class LocalAccess
{
    public static ClientOrigin Classify(IPAddress? peer, string? host, string? origin) =>
        !IsLoopback(peer) ? ClientOrigin.Network
        : IsLoopbackHost(host) && IsLoopbackOrigin(origin) ? ClientOrigin.ThisComputer
        : ClientOrigin.Rejected;

    /// <summary>127.0.0.0/8, ::1 and their IPv4-mapped IPv6 form (a dual-stack listener reports ::ffff:127.0.0.1).</summary>
    public static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written))
        {
            return false;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => written == 4 && bytes[0] == 127,
            AddressFamily.InterNetworkV6 => written == 16 && bytes[..15].IndexOfAnyExcept((byte)0) < 0 && bytes[15] == 1,
            _ => false,
        };
    }

    /// <summary>
    /// A <c>Host</c> header (or URI authority) naming this computer: <c>localhost</c>, a 127.x address or <c>[::1]</c>,
    /// with any port. A DNS rebinding page reaches 127.0.0.1 under its own name, which this rejects.
    /// </summary>
    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        var name = new HostString(host).Host;
        if (name.EndsWith('.'))
        {
            name = name[..^1];
        }

        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Length > 2 && name[0] == '[' && name[^1] == ']')
        {
            name = name[1..^1];
        }
        else if (name.Contains(':', StringComparison.Ordinal))
        {
            // an IPv6 address must be in brackets
            return false;
        }

        return IPAddress.TryParse(name, out var address) && IsLoopback(address);
    }

    /// <summary>
    /// No <c>Origin</c> (programs other than browsers), or a page served from this computer (the app's window, the UI
    /// in a browser here, a development server on another local port). <c>null</c> origins and other sites fail.
    /// </summary>
    public static bool IsLoopbackOrigin(string? origin) =>
        string.IsNullOrEmpty(origin) ||
        (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            uri.AbsolutePath == "/" && IsLoopbackHost(uri.Authority));
}

/// <summary>Refuses loopback requests whose <c>Host</c> or <c>Origin</c> isn't this computer (see <see cref="LocalAccess"/>).</summary>
public sealed partial class LocalRequestGuard(ILogger<LocalRequestGuard> logger)
{
    private readonly ILogger _logger = logger;

    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var headers = context.Request.Headers;
        var host = headers.Host.ToString();
        var origin = headers.Origin.ToString();
        if (LocalAccess.Classify(context.Connection.RemoteIpAddress, host, origin) != ClientOrigin.Rejected)
        {
            return next(context);
        }

        LogRefused(host, origin.Length == 0 ? "none" : origin);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a request from this computer for host '{Host}', origin '{Origin}': only pages served by this computer may use Backbloom (a website may have tried to control it)")]
    private partial void LogRefused(string host, string origin);
}
