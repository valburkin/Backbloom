using System.Text.Json.Nodes;

namespace Overglow.Server.Rpc;

/// <summary><c>sysinfo</c>: product and system information.</summary>
public sealed class SysInfoCommand(Payloads payloads) : IRpcCommand
{
    public string Name => "sysinfo";

    public RpcAccess Access => RpcAccess.Authorized;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Reply(payloads.SysInfo(), "sysinfo");
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// <c>serverinfo</c>: the state snapshot, then subscriptions to its <c>*-update</c> events. Always answers with the
/// info, also when only <c>performance-update</c> is subscribed (HyperHDR returned an empty reply then).
/// </summary>
public sealed class ServerInfoCommand(Payloads payloads, EventSubscriptions subscriptions) : IRpcCommand
{
    public string Name => "serverinfo";

    public RpcAccess Access => RpcAccess.Authorized;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Reply(payloads.ServerInfo(), "serverinfo");
        if (!context.Session.IsHttp && context.Message["subscribe"] is JsonArray names)
        {
            subscriptions.Subscribe(context.Session, names);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary><c>performance-counters</c>: an immediate <c>performance-update</c> for this session.</summary>
public sealed class PerformanceCountersCommand(Diagnostics.PerformanceMonitor performance) : IRpcCommand
{
    public string Name => "performance-counters";

    public RpcAccess Access => RpcAccess.Authorized;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Subcommand is not ("all" or "resources"))
        {
            context.Fail("Unknown subcommand", "performance-counters");
            return ValueTask.CompletedTask;
        }

        context.Reply(command: "performance-counters");
        if (context.Session.IsSubscribed("performance-update"))
        {
            context.Session.Send(RpcReplies.Event("performance-update", performance.Report()));
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary><c>instance</c>: there is exactly one lighting instance (0); switching to it succeeds.</summary>
public sealed class InstanceCommand : IRpcCommand
{
    public string Name => "instance";

    public RpcAccess Access => RpcAccess.Authorized;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Subcommand == "switchTo" && (context.GetInt("instance") ?? 0) == 0)
        {
            context.Reply(new JsonObject { ["instance"] = 0 });
        }
        else if (context.Subcommand == "switchTo")
        {
            context.Fail("Selected Overglow instance isn't running");
        }
        else
        {
            context.Fail("Overglow runs a single lighting pipeline; instance management isn't available");
        }

        return ValueTask.CompletedTask;
    }
}
