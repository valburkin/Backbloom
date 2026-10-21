using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Backbloom.Server.Auth;
using Backbloom.Server.Diagnostics;
using Backbloom.Server.Mqtt;
using Backbloom.Server.Rpc;
using Backbloom.Server.Transport;

namespace Backbloom.Server;

public static class BackbloomServer
{
    /// <summary>Registers the JSON-RPC commands, transports and their services.</summary>
    public static IServiceCollection AddBackbloomServer(this IServiceCollection services)
    {
        services.AddSingleton<AuthService>();
        services.AddSingleton<LocalRequestGuard>();
        services.AddSingleton<Payloads>();
        services.AddSingleton<PerformanceMonitor>();
        services.AddSingleton<EventSubscriptions>();
        services.AddSingleton<RpcDispatcher>();
        services.AddSingleton<SetupService>();
        services.AddSingleton<WebSocketRpcEndpoint>();
        services.AddSingleton<HttpRpcEndpoint>();
        services.AddSingleton<MqttIntegration>();
        services.AddHostedService(sp => sp.GetRequiredService<MqttIntegration>());

        services.AddSingleton<IRpcCommand, AuthorizeCommand>();
        services.AddSingleton<IRpcCommand, SysInfoCommand>();
        services.AddSingleton<IRpcCommand, ServerInfoCommand>();
        services.AddSingleton<IRpcCommand, ConfigCommand>();
        services.AddSingleton<IRpcCommand, SaveDbCommand>();
        services.AddSingleton<IRpcCommand, LoadDbCommand>();
        services.AddSingleton<IRpcCommand, AdjustmentCommand>();
        services.AddSingleton<IRpcCommand, ComponentStateCommand>();
        services.AddSingleton<IRpcCommand, ColorCommand>();
        services.AddSingleton<IRpcCommand, EffectCommand>();
        services.AddSingleton<IRpcCommand, EffectsCommand>();
        services.AddSingleton<IRpcCommand, ClearCommand>();
        services.AddSingleton<IRpcCommand, ClearAllCommand>();
        services.AddSingleton<IRpcCommand, SourceSelectCommand>();
        services.AddSingleton<IRpcCommand, LedDeviceCommand>();
        services.AddSingleton<IRpcCommand, LedColorsCommand>();
        services.AddSingleton<IRpcCommand, LoggingCommand>();
        services.AddSingleton<IRpcCommand, PerformanceCountersCommand>();
        services.AddSingleton<IRpcCommand, InstanceCommand>();
        services.AddSingleton<IRpcCommand, SetupCommand>();
        services.AddSingleton<IRpcCommand, ProfileCommand>();
        return services;
    }

    /// <summary>
    /// WebSocket JSON-RPC on any path, <c>POST /json-rpc</c>, and the UI's static files when
    /// <see cref="ServerOptions.UiDirectory"/> exists (see <see cref="UiFiles"/>); first of all, loopback requests
    /// that name another host or origin are refused (<see cref="LocalRequestGuard"/>).
    /// </summary>
    public static WebApplication UseBackbloomServer(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.Services.GetRequiredService<ServerOptions>();
        var guard = app.Services.GetRequiredService<LocalRequestGuard>();
        app.Use(guard.InvokeAsync);
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.Use(async (context, next) =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                await context.RequestServices.GetRequiredService<WebSocketRpcEndpoint>().HandleAsync(context);
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.Equals(HttpRpcEndpoint.Path, StringComparison.OrdinalIgnoreCase))
            {
                await context.RequestServices.GetRequiredService<HttpRpcEndpoint>().HandleAsync(context);
                return;
            }

            await next(context);
        });

        if (options.UiDirectory is { } ui && File.Exists(Path.Combine(ui, "index.html")))
        {
            app.UseUiFiles(ui);
        }

        return app;
    }
}
