using System.Text.Json.Nodes;
using Backbloom.Engine;
using Backbloom.Engine.Platform;
using Backbloom.Server.Auth;
using Backbloom.Settings;

namespace Backbloom.Server.Rpc;

/// <summary>
/// The first-run welcome and the network explanations as one state: whether the lighting was started once, why the
/// LEDs are held (HyperHDR running), what was imported, autostart, remote access, whether a password is set (remote
/// access needs one) and which explanations were read.
/// <see cref="Changed"/> drives the <c>setup-update</c> event.
/// </summary>
public sealed class SetupService : IDisposable
{
    private readonly LightingGate _gate;
    private readonly ISetupState _setup;
    private readonly INetworkAccess _network;
    private readonly AuthService _auth;
    private readonly IAutostart? _autostart;

    /// <param name="autostart">"Start with Windows"; null where the app has no desktop integration (headless, tests).</param>
    public SetupService(LightingGate gate, ISetupState setup, INetworkAccess network, AuthService auth, IAutostart? autostart = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(auth);
        _gate = gate;
        _setup = setup;
        _network = network;
        _auth = auth;
        _autostart = autostart;
        _gate.Changed += OnGateChanged;
        _network.Changed += OnNetworkChanged;
        _auth.PasswordChanged += OnNetworkChanged;
    }

    /// <summary>Raised after anything in <see cref="State"/> changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// <c>{ firstRunCompleted, outputHeld, otherApp: { name, executable } | null, importedFrom, autostart: { available,
    /// enabled }, remoteAccess: { enabled, fixedByCommandLine, port, urls, advertised }, passwordSet, acknowledged: [topics] }</c>
    /// </summary>
    public JsonObject State()
    {
        var gate = _gate.Status;
        var network = _network.Status;
        var urls = new JsonArray();
        foreach (var url in network.Urls)
        {
            urls.Add((JsonNode)url);
        }

        var acknowledged = new JsonArray();
        if (_setup.IsAcknowledged(SetupTopics.Discovery))
        {
            acknowledged.Add((JsonNode)SetupTopics.Discovery);
        }

        return new JsonObject
        {
            ["firstRunCompleted"] = gate.FirstRunCompleted,
            ["outputHeld"] = gate.OutputHeld,
            ["otherApp"] = gate.OtherApp is { } app ? new JsonObject { ["name"] = app.Name, ["executable"] = app.Executable } : null,
            ["importedFrom"] = _setup.ImportedFrom,
            ["autostart"] = new JsonObject { ["available"] = _autostart is not null, ["enabled"] = _autostart?.IsEnabled ?? false },
            ["remoteAccess"] = new JsonObject
            {
                ["enabled"] = network.Enabled,
                ["fixedByCommandLine"] = network.FixedByCommandLine,
                ["port"] = network.Port,
                ["urls"] = urls,
                ["advertised"] = network.Advertised,
            },
            ["passwordSet"] = _auth.HasPassword,
            ["acknowledged"] = acknowledged,
        };
    }

    /// <summary>"Start lighting": optionally sets autostart, then completes the first run (the LEDs follow unless HyperHDR runs).</summary>
    public void Start(bool? autostart)
    {
        if (autostart is { } enable)
        {
            ApplyAutostart(enable);
        }

        // a change of the gate raises Changed itself; a change of autostart alone needs its own event
        var before = _gate.Status;
        if (_gate.CompleteFirstRun() == before && autostart is not null)
        {
            Changed?.Invoke();
        }
    }

    /// <returns>False when this app can't start with the OS.</returns>
    public bool SetAutostart(bool enabled)
    {
        if (_autostart is null)
        {
            return false;
        }

        ApplyAutostart(enabled);
        Changed?.Invoke();
        return true;
    }

    public void Acknowledge(string topic)
    {
        if (!_setup.IsAcknowledged(topic))
        {
            _setup.Acknowledge(topic);
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _gate.Changed -= OnGateChanged;
        _network.Changed -= OnNetworkChanged;
        _auth.PasswordChanged -= OnNetworkChanged;
    }

    private void ApplyAutostart(bool enabled)
    {
        if (_autostart is { } autostart && autostart.IsEnabled != enabled)
        {
            autostart.SetEnabled(enabled);
        }
    }

    private void OnGateChanged(LightingGateStatus status) => Changed?.Invoke();

    private void OnNetworkChanged() => Changed?.Invoke();
}

/// <summary>
/// <c>setup</c> (a Backbloom addition): <c>getstate</c> (any authorized session), and for admins <c>start</c>
/// <c>{ autostart?: bool }</c> (the welcome's "Start lighting"), <c>autostart</c> <c>{ enabled }</c> and
/// <c>acknowledge</c> <c>{ topic: "discovery" }</c>. Every subcommand replies with the state (see
/// <see cref="SetupService.State"/>); remote access itself is the <c>network.remoteAccess</c> setting.
/// </summary>
public sealed class SetupCommand(SetupService setup) : IRpcCommand
{
    public string Name => "setup";

    public RpcAccess Access => RpcAccess.Authorized;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Subcommand is not "getstate" && !context.Session.Admin)
        {
            context.Fail("No Authorization");
            return ValueTask.CompletedTask;
        }

        switch (context.Subcommand)
        {
            case "getstate":
                context.Reply(setup.State());
                break;
            case "start":
                setup.Start(context.GetBool("autostart"));
                context.Reply(setup.State());
                break;
            case "autostart" when context.GetBool("enabled") is not { } enabled:
                context.Fail("enabled (true or false) is required");
                break;
            case "autostart":
                if (setup.SetAutostart(context.GetBool("enabled")!.Value))
                {
                    context.Reply(setup.State());
                }
                else
                {
                    context.Fail("This copy of Backbloom can't start with the system");
                }

                break;
            case "acknowledge" when !SetupTopics.IsKnown(context.GetString("topic")):
                context.Fail($"topic must be '{SetupTopics.Discovery}'");
                break;
            case "acknowledge":
                setup.Acknowledge(context.GetString("topic")!);
                context.Reply(setup.State());
                break;
            default:
                context.Fail("unknown or missing subcommand");
                break;
        }

        return ValueTask.CompletedTask;
    }
}
