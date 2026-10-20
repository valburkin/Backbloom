using System.Text.Json.Nodes;
using Backbloom.Capture.Hdr;
using Backbloom.Engine;
using Backbloom.Server.Auth;
using Backbloom.Server.Diagnostics;
using Backbloom.Settings;

namespace Backbloom.Server.Rpc;

/// <summary>
/// Connects a session to the events it asked for in <c>serverinfo.subscribe</c> (port of CallbackAPI::subscribeFor).
/// Every subscription is an <see cref="IDisposable"/> owned by the session, so closing a connection detaches it.
/// </summary>
public sealed class EventSubscriptions(
    LightingEngine engine,
    ISettingsStore settings,
    AuthService auth,
    Payloads payloads,
    PerformanceMonitor performance,
    SetupService setup)
{
    /// <summary>Every name the protocol knows; unknown names are ignored like in HyperHDR.</summary>
    public static readonly IReadOnlyList<string> Available =
    [
        "components-update", "performance-update", "sessions-update", "priorities-update", "imageToLedMapping-update",
        "adjustment-update", "leds-colors", "live-video", "settings-update", "leds-update", "instance-update", "token-update",
        "controllers-update", "capture-update", "setup-update",
    ];

    private static readonly TimeSpan s_performanceInterval = TimeSpan.FromSeconds(4);

    /// <summary>Subscribes to every requested event the session may receive ("all" expands to everything).</summary>
    public void Subscribe(RpcSession session, JsonArray names)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(names);
        var list = names.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : null).OfType<string>().ToList();
        if (list.Contains("all"))
        {
            list = [.. Available];
        }

        foreach (var name in list.Distinct(StringComparer.Ordinal))
        {
            if ((name is "settings-update" or "token-update") && !session.Admin)
            {
                continue;
            }

            if (Create(session, name) is { } subscription)
            {
                session.Subscribe(name, subscription);
            }
        }
    }

    private IDisposable? Create(RpcSession session, string name)
    {
        switch (name)
        {
            case "components-update":
                {
                    void Handler(Component c, bool on) => session.Send(RpcReplies.Event(name, new JsonObject { ["name"] = c.ToId(), ["enabled"] = on }));
                    engine.Components.Changed += Handler;
                    return new Subscription(() => engine.Components.Changed -= Handler);
                }

            case "priorities-update":
                {
                    void Handler() => session.Send(RpcReplies.Event(name, payloads.PrioritiesInfo()));
                    engine.PrioritiesChanged += Handler;
                    return new Subscription(() => engine.PrioritiesChanged -= Handler);
                }

            case "imageToLedMapping-update":
                {
                    void Handler(string type) => session.Send(RpcReplies.Event(name, new JsonObject { ["imageToLedMappingType"] = type }));
                    engine.MappingTypeChanged += Handler;
                    return new Subscription(() => engine.MappingTypeChanged -= Handler);
                }

            case "adjustment-update":
                {
                    void Handler(JsonArray adjustment) => session.Send(RpcReplies.Event(name, adjustment.DeepClone()));
                    engine.AdjustmentChanged += Handler;
                    return new Subscription(() => engine.AdjustmentChanged -= Handler);
                }

            case "leds-update":
                {
                    void Handler(JsonArray leds) => session.Send(RpcReplies.Event(name, new JsonObject { ["leds"] = leds.DeepClone() }));
                    engine.LedsChanged += Handler;
                    return new Subscription(() => engine.LedsChanged -= Handler);
                }

            case "controllers-update":
                {
                    // serverinfo.controllers again: after a layout change and when a controller changes state
                    void Handler() => session.Send(RpcReplies.Event(name, new JsonObject { ["controllers"] = payloads.Controllers() }));
                    void DeviceHandler(DeviceInfo _) => Handler();
                    engine.ControllersChanged += Handler;
                    engine.DeviceChanged += DeviceHandler;
                    return new Subscription(() =>
                    {
                        engine.ControllersChanged -= Handler;
                        engine.DeviceChanged -= DeviceHandler;
                    });
                }

            case "capture-update":
                {
                    // serverinfo.systemGrabbers again when the screen capture starts or stops or the monitor's HDR mode changed
                    void Handler(CaptureHdrStatus _) => session.Send(RpcReplies.Event(name, payloads.SystemGrabbers()));
                    engine.CaptureHdrChanged += Handler;
                    return new Subscription(() => engine.CaptureHdrChanged -= Handler);
                }

            case "settings-update":
                {
                    void Handler(SettingsChange change)
                    {
                        foreach (var (section, value) in change.Sections)
                        {
                            session.Send(RpcReplies.Event(name, new JsonObject { [section] = value.DeepClone() }));
                        }
                    }

                    settings.Changed += Handler;
                    return new Subscription(() => settings.Changed -= Handler);
                }

            case "token-update":
                {
                    void Handler(IReadOnlyList<TokenInfo> tokens) => session.Send(RpcReplies.Event(name, AuthorizeCommand.TokenList(tokens)));
                    auth.TokensChanged += Handler;
                    return new Subscription(() => auth.TokensChanged -= Handler);
                }

            case "setup-update":
                {
                    // the setup command's state: first run, HyperHDR running, autostart, remote access
                    void Handler() => session.Send(RpcReplies.Event(name, setup.State()));
                    setup.Changed += Handler;
                    return new Subscription(() => setup.Changed -= Handler);
                }

            case "performance-update":
                return new PeriodicPush(s_performanceInterval, () => session.Send(RpcReplies.Event(name, performance.Report())), immediately: true);

            case "instance-update":
            case "sessions-update":
                // One instance and no peer discovery: these never change, so there is nothing to send.
                return new Subscription(() => { });

            default:
                return null;
        }
    }
}

/// <summary>Runs an action periodically on the thread pool until disposed.</summary>
public sealed class PeriodicPush : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    public PeriodicPush(TimeSpan interval, Action push, bool immediately = false)
    {
        ArgumentNullException.ThrowIfNull(push);
        _ = RunAsync(interval, push, immediately, _stop.Token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private static async Task RunAsync(TimeSpan interval, Action push, bool immediately, CancellationToken token)
    {
        try
        {
            if (immediately)
            {
                push();
            }

            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(token))
            {
                push();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
