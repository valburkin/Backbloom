using System.Text.Json.Nodes;

namespace Overglow.Devices;

/// <summary>
/// The driver tools behind <c>leddevice discover / getProperties / identify</c>: they run for a driver type, not for
/// the configured controller, so they work before anything is set up.
/// </summary>
public sealed class DeviceTools(DriverRegistry registry, DriverContext context)
{
    /// <summary><c>{ ledDeviceType, devices: [{ value, name, ... }] }</c>; an empty list for drivers that can't discover.</summary>
    public async Task<JsonObject> DiscoverAsync(string type, JsonObject? parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        var devices = registry.TryGet(type, out var d) && d.Discover is { } discover
            ? await discover(new DeviceRequest(parameters ?? [], context, cancellationToken))
            : [];
        return new JsonObject { ["ledDeviceType"] = type, ["devices"] = devices };
    }

    /// <summary><c>{ properties: {...} }</c>; empty properties for drivers that have none.</summary>
    public async Task<JsonObject> GetPropertiesAsync(string type, JsonObject? parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        var properties = registry.TryGet(type, out var d) && d.GetProperties is { } get
            ? await get(new DeviceRequest(parameters ?? [], context, cancellationToken))
            : [];
        return new JsonObject { ["properties"] = properties };
    }

    /// <summary>Asks one light of the given driver to signal itself; false when the driver can't.</summary>
    public async Task<bool> IdentifyAsync(string type, JsonObject? parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!registry.TryGet(type, out var d) || d.Identify is not { } identify)
        {
            return false;
        }

        await identify(new DeviceRequest(parameters ?? [], context, cancellationToken));
        return true;
    }
}
