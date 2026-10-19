namespace Overglow.Server;

/// <summary>Web server settings supplied by the host.</summary>
public sealed record ServerOptions
{
    /// <summary>Directory with the built React UI (ui/dist); not served when null or missing.</summary>
    public string? UiDirectory { get; init; }

    /// <summary>Product version reported by <c>sysinfo</c>.</summary>
    public string Version { get; init; } = "0.1.0";

    /// <summary>Build identifier reported by <c>sysinfo</c>.</summary>
    public string Build { get; init; } = "dev";

    /// <summary>Build time reported by <c>sysinfo</c>.</summary>
    public string BuildTime { get; init; } = string.Empty;

    /// <summary>Stable id of this installation (persisted by the host).</summary>
    public string InstanceId { get; init; } = Guid.Empty.ToString();

    /// <summary>Largest JSON-RPC message accepted (a full layout is about 100 KB).</summary>
    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;
}
