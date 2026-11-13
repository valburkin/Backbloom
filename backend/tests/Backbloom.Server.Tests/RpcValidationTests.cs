using System.Text.Json.Nodes;

namespace Backbloom.Server.Tests;

/// <summary>
/// Requests the JSON-RPC API refuses, and why: clients (the UI, Home Assistant, scripts written against HyperHDR's
/// API) get an error naming what is wrong instead of a half-applied change.
/// </summary>
public sealed class RpcValidationTests(RpcValidationTests.SharedServer shared) : IClassFixture<RpcValidationTests.SharedServer>
{
    public sealed class SharedServer : IAsyncLifetime
    {
        internal TestServer Server { get; private set; } = null!;

        public async ValueTask InitializeAsync() => Server = await TestServer.StartAsync();

        public async ValueTask DisposeAsync() => await Server.DisposeAsync();
    }

    [Theory]
    [InlineData("color", null, """{ "priority": 50, "color": [255, 0] }""", "color must be an array of at least three numbers ([r, g, b, ...])")]
    [InlineData("color", null, """{ "priority": 50, "color": [255, 0, "red"] }""", "color must be an array of at least three numbers ([r, g, b, ...])")]
    [InlineData("color", null, """{ "priority": 50, "color": [255, 0, 0], "origin": "UI" }""", "origin must have 4 to 20 characters")]
    [InlineData("clear", null, "{}", "priority is required")]
    [InlineData("sourceselect", null, "{}", "Priority request is invalid")]
    [InlineData("effect", null, """{ "priority": 50, "effect": {} }""", "effect.name is required")]
    [InlineData("effect", null, """{ "priority": 50, "effect": { "name": "Breath" }, "origin": "a name that is far too long" }""", "origin must have 4 to 20 characters")]
    [InlineData("effect", null, """{ "priority": 50, "effect": { "name": "No such effect" } }""", "Effect 'No such effect' not found")]
    [InlineData("config", "setconfig", """{ "config": [1, 2] }""", "The config parameter must be an object")]
    [InlineData("config", "reload-everything", "{}", "unknown or missing subcommand")]
    [InlineData("adjustment", null, """{ "adjustment": 5 }""", "The adjustment parameter must be an object")]
    [InlineData("authorize", "becomeAdmin", "{}", "Command not implemented")]
    [InlineData("authorize", "deleteToken", """{ "id": "nope" }""", "No token with this id")]
    [InlineData("performance-counters", "gpu", "{}", "Unknown subcommand")]
    [InlineData("instance", "switchTo", """{ "instance": 1 }""", "Selected Backbloom instance isn't running")]
    [InlineData("instance", "startInstance", """{ "instance": 1 }""", "Backbloom runs a single lighting pipeline; instance management isn't available")]
    public async Task InvalidRequests_FailWithAReason(string command, string? subcommand, string parameters, string error)
    {
        await using var client = await shared.Server.ConnectAsync();

        var reply = await client.CallAsync(command, subcommand, JsonNode.Parse(parameters)!.AsObject());

        Assert.False(reply["success"]!.GetValue<bool>());
        Assert.Equal(error, reply["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task TheOneInstance_CanBeSwitchedTo()
    {
        await using var client = await shared.Server.ConnectAsync();

        var reply = await client.CallAsync("instance", "switchTo", new JsonObject { ["instance"] = 0 });

        Assert.True(reply["success"]!.GetValue<bool>());
        Assert.Equal(0, reply["info"]!["instance"]!.GetValue<int>());
    }

    [Fact]
    public async Task PerformanceCounters_PushAReportToSubscribers()
    {
        await using var client = await shared.Server.ConnectAsync();
        await client.CallAsync("serverinfo", parameters: new JsonObject { ["subscribe"] = new JsonArray("performance-update") });
        client.NextEvent("performance-update");

        Assert.True((await client.CallAsync("performance-counters", "all"))["success"]!.GetValue<bool>());

        // the immediate report comes before the next periodic one (every 4 s)
        Assert.NotNull(client.NextEvent("performance-update", timeoutMs: 2000)["data"]);
    }
}
