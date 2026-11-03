using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Backbloom.Devices;
using Backbloom.Devices.Drivers.Serial;
using Backbloom.Devices.Setup;
using Backbloom.Engine;
using Backbloom.Engine.Platform;

namespace Backbloom.Server.Rpc;

/// <summary>
/// <c>hardware</c> (a Backbloom addition, admins only): the guided hardware setup (docs/guided-setup.md).
/// <c>screens</c> (monitor sizes), <c>usb-scan</c> / <c>usb-networks</c> / <c>usb-wifi</c> (Improv Wi-Fi over USB
/// serial), <c>wled-read</c> / <c>wled-write</c> / <c>wled-wifi</c> / <c>wled-test</c> (a WLED board's JSON API),
/// <c>find</c> (one search over USB, the network and the boards' hotspots) and <c>connect-wifi</c> (a found board onto
/// the home network, with <c>hardware-progress-update</c> events to the asking connection).
/// Parameters are top-level properties of the request (also accepted inside <c>params</c>). Only the port or board the
/// request names is contacted; ports configured devices use and boards a running device streams to are refused. Over
/// a WebSocket the subcommands that talk to a board answer when they are done without holding up the connection's
/// other requests (joining a Wi-Fi network takes up to 30 s). Wi-Fi passwords are never logged or stored.
/// </summary>
public sealed partial class HardwareCommand(
    LightingEngine engine,
    DriverRegistry registry,
    DriverContext driverContext,
    UsbBoards usb,
    WledSetup wled,
    BoardFinder finder,
    IDisplayList displays,
    ILogger<HardwareCommand> logger) : IRpcCommand
{
    private const string SerialGroup = "leds_group_3_serial";

    /// <summary>The device types that stream to an LED board by its <c>host</c> (a WLED board can take any of them).</summary>
    private static readonly HashSet<string> s_streamingTypes = new(StringComparer.Ordinal) { "wled", "ddp", "udpe131", "udpartnet" };

    private static readonly ConditionalWeakTable<object, BoardSteps> s_boardSteps = new();

    public string Name => "hardware";

    public RpcAccess Access => RpcAccess.Admin;

    public async ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (context.Subcommand)
        {
            case "screens":
                context.Reply(Screens());
                return;
            case "find" or "connect-wifi" or "wled-identify" or "wled-probe-start" or "wled-probe-end" or "usb-scan" or "usb-networks" or "usb-wifi" or "wled-read" or "wled-write" or "wled-wifi" or "wled-test":
                break;
            default:
                context.Fail("Unknown or missing subcommand");
                return;
        }

        if (context.Session.IsHttp)
        {
            await RunAsync(context);
        }
        else if (context.Subcommand.StartsWith("wled-", StringComparison.Ordinal))
        {
            // the reply goes out when the board is done; the connection's other requests don't wait for it, but its
            // board steps run in the order they came (an `off` sent right after a check must not overtake it)
            var steps = s_boardSteps.GetValue(context.Session, _ => new BoardSteps());
            lock (steps.Lock)
            {
                steps.Tail = steps.Tail.ContinueWith(_ => RunAsync(context), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }
        }
        else
        {
            // the reply goes out when the search or Wi-Fi step is done; the connection's next requests don't wait for it
            _ = Task.Run(() => RunAsync(context));
        }
    }

    // Each connection's board steps (wled-*), one after the other.
    private sealed class BoardSteps
    {
        public Lock Lock { get; } = new();

        public Task Tail { get; set; } = Task.CompletedTask;
    }

    private async Task RunAsync(RpcContext context)
    {
        // a request outlives the connection's token when it runs on its own: the steps have their own time limits
        var cancellationToken = context.Session.IsHttp ? context.CancellationToken : CancellationToken.None;
        try
        {
            context.Reply(context.Subcommand switch
            {
                "find" => await FindAsync(cancellationToken),
                "connect-wifi" => await ConnectWifiAsync(context, cancellationToken),
                "wled-identify" => await WledIdentifyAsync(context, cancellationToken),
                "wled-probe-start" => await WledProbeStartAsync(context, cancellationToken),
                "wled-probe-end" => await WledProbeEndAsync(context, cancellationToken),
                "usb-scan" => await UsbScanAsync(cancellationToken),
                "usb-networks" => await UsbNetworksAsync(context, cancellationToken),
                "usb-wifi" => await UsbWifiAsync(context, cancellationToken),
                "wled-read" => await wled.ReadAsync(Host(context), cancellationToken),
                "wled-write" => await WledWriteAsync(context, Request(context), cancellationToken),
                "wled-wifi" => await WledWriteAsync(context, new JsonObject { ["wifi"] = new JsonObject { ["ssid"] = Ssid(context), ["password"] = Param(context, "password") is JsonValue p && p.TryGetValue(out string? s) ? s : null } }, cancellationToken),
                "wled-test" => await WledTestAsync(context, cancellationToken),
                _ => throw new InvalidOperationException(),
            });
        }
        catch (HardwareSetupException ex)
        {
            context.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException)
        {
            // no socket details: they would tell what listens where
            context.Fail($"{context.Subcommand} failed: couldn’t reach the board");
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            context.Fail($"{context.Subcommand} failed: {ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // the connection closed
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(ex, context.FullCommand);
            context.Fail($"Internal error: {ex.Message}");
        }
    }

    private async Task<JsonObject> FindAsync(CancellationToken cancellationToken)
    {
        var found = await finder.FindAsync(SerialPortsInUse(), cancellationToken);
        var boards = new JsonArray();
        foreach (var b in found.Boards)
        {
            boards.Add((JsonNode)new JsonObject
            {
                ["id"] = b.Id,
                ["via"] = b.Via,
                ["name"] = b.Name,
                ["isNew"] = b.IsNew,
                ["port"] = b.Port,
                ["host"] = b.Host,
                ["hotspot"] = b.Hotspot,
                ["mac"] = b.Mac,
                ["version"] = b.Version,
                ["ledCount"] = b.LedCount,
                ["setUp"] = b.SetUp,
                ["portName"] = b.PortName,
                ["firmware"] = b.Firmware,
                ["chip"] = b.Chip,
                ["deviceName"] = b.DeviceName,
                ["signal"] = b.Signal,
                ["mdnsName"] = b.MdnsName,
            });
        }

        var silent = new JsonArray();
        foreach (var p in found.UsbSilent)
        {
            silent.Add((JsonNode)new JsonObject { ["port"] = p.PortName, ["name"] = p.Description.Length > 0 ? p.Description : p.PortName });
        }

        return new JsonObject
        {
            ["boards"] = boards,
            ["wifi"] = new JsonObject
            {
                ["adapter"] = found.Wifi.Adapter,
                ["currentSsid"] = found.Wifi.Ssid,
                ["blocked"] = found.Wifi.Blocked == WifiBlock.Location ? "location" : null,
                ["savedPassword"] = found.SavedPassword,
            },
            ["usbSilent"] = silent,
        };
    }

    private async Task<JsonObject> WledIdentifyAsync(RpcContext context, CancellationToken cancellationToken)
    {
        var host = Host(context);
        await RefuseStreamingAsync(host, cancellationToken);
        await wled.IdentifyAsync(host, cancellationToken);
        return [];
    }

    private async Task<JsonObject> WledProbeStartAsync(RpcContext context, CancellationToken cancellationToken)
    {
        var host = Host(context);
        var round = Number(context, "round") ?? 1;
        await RefuseStreamingAsync(host, cancellationToken);
        var candidates = new JsonArray();
        foreach (var (output, pin) in await wled.ProbeStartAsync(host, round, cancellationToken))
        {
            candidates.Add((JsonNode)new JsonObject { ["output"] = output, ["pin"] = pin });
        }

        return new JsonObject { ["candidates"] = candidates };
    }
    // { id } from the last find, or { via, port | hotspot | host }
    private async Task<JsonObject> ConnectWifiAsync(RpcContext context, CancellationToken cancellationToken)
    {
        FoundBoard board;
        if (Param(context, "id") is JsonValue idValue && idValue.TryGetValue(out string? id) && id.Length > 0)
        {
            board = finder.Known(id) ?? Target(id);
        }
        else
        {
            var via = RequiredString(context, "via");
            board = via switch
            {
                BoardRoutes.Usb => new FoundBoard($"usb:{RequiredString(context, "port")}", via, RequiredString(context, "port"), true) { Port = RequiredString(context, "port") },
                BoardRoutes.Hotspot => new FoundBoard($"hotspot:{RequiredString(context, "hotspot")}", via, RequiredString(context, "hotspot"), true) { Hotspot = RequiredString(context, "hotspot") },
                BoardRoutes.Network => new FoundBoard($"network:{RequiredString(context, "host")}", via, RequiredString(context, "host"), false) { Host = RequiredString(context, "host") },
                _ => throw new HardwareSetupException("via must be usb, network or hotspot"),
            };
        }

        var ssid = Ssid(context);
        var useSaved = Param(context, "useSavedPassword") is JsonValue u && u.TryGetValue(out bool saved) && saved;
        var password = useSaved ? null : Param(context, "password") is JsonValue p && p.TryGetValue(out string? s) ? s : string.Empty;
        void Progress(string step) =>
            context.Session.Send(RpcReplies.Event("hardware-progress-update", new JsonObject { ["tan"] = context.Tan, ["id"] = board.Id, ["step"] = step }));

        var joined = await finder.JoinWifiAsync(board, ssid, password, SerialPortsInUse(), Progress, cancellationToken, useSaved);
        return new JsonObject { ["host"] = joined.Host, ["address"] = joined.Address, ["name"] = joined.Name, ["mac"] = joined.Mac };
    }

    // an id the last search didn't return (the UI kept it): its route and target are in it
    private static FoundBoard Target(string id)
    {
        var colon = id.IndexOf(':', StringComparison.Ordinal);
        var (via, target) = colon > 0 ? (id[..colon], id[(colon + 1)..]) : (string.Empty, string.Empty);
        return via switch
        {
            BoardRoutes.Usb when target.Length > 0 => new FoundBoard(id, via, target, true) { Port = target },
            BoardRoutes.Hotspot when target.Length > 0 => new FoundBoard(id, via, target, true) { Hotspot = target },
            _ => throw new HardwareSetupException("Search again: that board isn’t there any more"),
        };
    }
    private JsonObject Screens()
    {
        var list = new JsonArray();
        foreach (var d in displays.GetDisplays())
        {
            list.Add((JsonNode)new JsonObject
            {
                ["name"] = d.Name,
                ["primary"] = d.Primary,
                ["widthMm"] = d.WidthMm,
                ["heightMm"] = d.HeightMm,
                ["device"] = d.Device,
                ["devicePath"] = d.DevicePath,
            });
        }

        return new JsonObject { ["screens"] = list };
    }

    private async Task<JsonObject> UsbScanAsync(CancellationToken cancellationToken)
    {
        var scan = await usb.ScanAsync(SerialPortsInUse(), cancellationToken);
        var boards = new JsonArray();
        foreach (var b in scan.Boards)
        {
            boards.Add((JsonNode)new JsonObject
            {
                ["port"] = b.Port,
                ["name"] = b.Name,
                ["state"] = StateName(b.State),
                ["firmware"] = b.Info?.Firmware,
                ["version"] = b.Info?.Version,
                ["chip"] = b.Info?.Chip,
                ["deviceName"] = b.Info?.DeviceName,
                ["address"] = b.Address,
            });
        }

        var silent = new JsonArray();
        foreach (var p in scan.Silent)
        {
            silent.Add((JsonNode)new JsonObject { ["port"] = p.PortName, ["name"] = p.Description.Length > 0 ? p.Description : p.PortName });
        }

        return new JsonObject { ["boards"] = boards, ["silent"] = silent };
    }

    private async Task<JsonObject> UsbNetworksAsync(RpcContext context, CancellationToken cancellationToken)
    {
        var networks = new JsonArray();
        foreach (var n in await usb.NetworksAsync(RequiredString(context, "port"), SerialPortsInUse(), cancellationToken))
        {
            networks.Add((JsonNode)new JsonObject { ["ssid"] = n.Ssid, ["signal"] = n.SignalPercent, ["secure"] = n.Secure, ["rssi"] = n.Rssi });
        }

        return new JsonObject { ["networks"] = networks };
    }

    private async Task<JsonObject> UsbWifiAsync(RpcContext context, CancellationToken cancellationToken)
    {
        var port = RequiredString(context, "port");
        var ssid = Ssid(context);
        var password = Param(context, "password") is JsonValue p && p.TryGetValue(out string? s) ? s : string.Empty;
        var address = await usb.JoinWifiAsync(port, ssid, password, SerialPortsInUse(), cancellationToken);
        var host = Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Authority : address;
        return new JsonObject { ["address"] = address, ["host"] = host };
    }

    private async Task<JsonObject> WledWriteAsync(RpcContext context, JsonObject request, CancellationToken cancellationToken)
    {
        var host = Host(context);
        await RefuseStreamingAsync(host, cancellationToken);
        return new JsonObject { ["restarting"] = await wled.WriteAsync(host, request, cancellationToken) };
    }

    private async Task<JsonObject> WledProbeEndAsync(RpcContext context, CancellationToken cancellationToken)
    {
        var host = Host(context);
        var keep = Param(context, "keep") is JsonValue k && k.TryGetValue(out bool b) && b;
        if (!keep)
        {
            // putting the settings back writes to the board
            await RefuseStreamingAsync(host, cancellationToken);
        }

        return new JsonObject { ["restarting"] = await wled.ProbeEndAsync(host, keep, cancellationToken) };
    }

    private async Task<JsonObject> WledTestAsync(RpcContext context, CancellationToken cancellationToken)
    {
        var host = Host(context);
        var patternName = Param(context, "pattern") is JsonValue v && v.TryGetValue(out string? n) ? n : null;
        if (!Enum.TryParse<WledTestPattern>(patternName, ignoreCase: true, out var pattern) || int.TryParse(patternName, out _))
        {
            throw new HardwareSetupException("pattern must be first, range, color, white, points or off");
        }

        var output = Number(context, "output") ?? 0;
        var count = Number(context, "count");
        (byte, byte, byte)? color = null;
        if (Param(context, "color") is { } c)
        {
            if (c is not JsonArray { Count: 3 } rgb || rgb.Any(x => x is not JsonValue cv || !cv.TryGetValue(out double d) || d < 0 || d > 255))
            {
                throw new HardwareSetupException("color must be [r, g, b] with values 0 to 255");
            }

            color = ((byte)rgb[0]!.GetValue<double>(), (byte)rgb[1]!.GetValue<double>(), (byte)rgb[2]!.GetValue<double>());
        }

        List<int>? leds = null;
        if (Param(context, "leds") is { } l)
        {
            if (l is not JsonArray list || list.Any(x => x is not JsonValue lv || !lv.TryGetValue(out int _)))
            {
                throw new HardwareSetupException("leds must be a list of LED numbers");
            }

            leds = [.. list.Select(x => x!.GetValue<int>())];
        }

        if (pattern != WledTestPattern.Off)
        {
            await RefuseStreamingAsync(host, cancellationToken);
        }

        await wled.TestAsync(host, output, pattern, count, color, leds, cancellationToken);
        return [];
    }

    // A running device of the layout that sends to this host (WLED, DDP, E1.31, Art-Net to the same board). A device
    // whose own address can't be looked up right now may be sending to it as well: refused too.
    private async Task RefuseStreamingAsync(string host, CancellationToken cancellationToken)
    {
        // devices that send to a board and are connected (or connecting) to it; one waiting to retry sends nothing
        var connected = engine.Devices
            .Where(d => d.Running && d.State is DeviceState.Opening or DeviceState.Ready && s_streamingTypes.Contains(d.Type))
            .ToDictionary(d => d.Id, d => d.State, StringComparer.Ordinal);
        IPAddress? target = null;
        var targetLooked = false;
        foreach (var controller in engine.Setup.Controllers.Where(c => connected.ContainsKey(c.Id)))
        {
            var configured = DeviceConfig.GetString(controller.Device, "host");
            if (configured.Length == 0)
            {
                continue;
            }

            var x = DeviceConfig.SplitHostPort(configured).Host;
            var y = DeviceConfig.SplitHostPort(host).Host;
            var same = string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
            if (!same)
            {
                if (!targetLooked)
                {
                    // a request for a host that can't be found fails later, when the board is asked
                    target = await TryResolveAsync(y, cancellationToken);
                    targetLooked = true;
                }

                if (target is null)
                {
                    continue;
                }

                // the shared resolver falls back to the last address the name had (the one the device connected to)
                if (await TryResolveAsync(x, cancellationToken) is not { } address)
                {
                    if (connected[controller.Id] != DeviceState.Ready)
                    {
                        // still connecting to an address nobody knows: it sends nothing yet
                        continue;
                    }

                    throw new HardwareSetupException($"Backbloom may be sending colors to this board (the device \"{controller.Name}\" at {configured}, which can’t be found right now): turn that device off first");
                }

                same = address.Equals(target);
            }

            if (same)
            {
                throw new HardwareSetupException($"Backbloom is sending colors to this board (the device \"{controller.Name}\"): turn that device off first");
            }
        }
    }

    private async Task<IPAddress?> TryResolveAsync(string name, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(name, out var literal))
        {
            return literal;
        }

        try
        {
            return await driverContext.ResolveHost(name, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
    }
    /// <summary>
    /// Serial ports the layout's serial devices use: the port they name, or for "auto" the one the driver would pick
    /// (whether or not the device runs).
    /// </summary>
    private HashSet<string> SerialPortsInUse()
    {
        var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<Backbloom.Discovery.Usb.SerialPortInfo>? ports = null;
        foreach (var controller in engine.Setup.Controllers)
        {
            var type = DeviceConfig.GetString(controller.Device, "type");
            if (!registry.TryGet(type, out var driver) || driver.Group != SerialGroup)
            {
                continue;
            }

            var options = SerialOptions.FromConfig(controller.Device, 115200);
            if (!options.IsAuto)
            {
                inUse.Add(options.Output);
                continue;
            }

            ports ??= driverContext.Discovery.SerialPorts.GetPorts();
            if (SerialLedDriver.PickAutomatically(ports, options.EspHandshake) is { } picked)
            {
                inUse.Add(picked.PortName);
            }
        }

        return inUse;
    }

    private static string StateName(ImprovState state) => state switch
    {
        ImprovState.Ready => "ready",
        ImprovState.Provisioned => "provisioned",
        ImprovState.AuthorizationRequired => "authorize",
        ImprovState.Provisioning => "provisioning",
        _ => "unavailable",
    };

    private static JsonNode? Param(RpcContext context, string name) => context.Message[name] ?? (context.Message["params"] as JsonObject)?[name];

    private static JsonObject Request(RpcContext context) => context.Message["params"] as JsonObject ?? context.Message;

    private static string Host(RpcContext context) => RequiredString(context, "host");

    private static string RequiredString(RpcContext context, string name) =>
        Param(context, name) is JsonValue v && v.TryGetValue(out string? s) && s.Trim().Length > 0
            ? s.Trim()
            : throw new HardwareSetupException($"{name} is required");

    // network names may start or end with spaces: taken as they are, only an empty one is refused
    private static string Ssid(RpcContext context) =>
        Param(context, "ssid") is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : throw new HardwareSetupException("ssid is required");

    private static int? Number(RpcContext context, string name) => Param(context, name) switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out double d) && d == Math.Floor(d) && Math.Abs(d) < 1e6 => (int)d,
        _ => throw new HardwareSetupException($"{name} must be a whole number"),
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "JSON-RPC command {Command} failed")]
    private partial void LogFailed(Exception exception, string command);
}
