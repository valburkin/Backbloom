using System.Text.Json.Nodes;

namespace Overglow.Server.Tests;

/// <summary>The <c>setup</c> command and the <c>setup-update</c> event the first-run welcome uses.</summary>
public sealed class SetupRpcTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public async Task Welcome_StateStartAndEvent()
    {
        await using var server = await TestServer.StartAsync(firstRun: true);
        await using var client = await server.ConnectAsync();
        await client.CallAsync("serverinfo", null, Obj("""{ "subscribe": ["setup-update"] }"""));

        var state = (await client.CallAsync("setup", "getstate"))["info"]!;
        Assert.False(state["firstRunCompleted"]!.GetValue<bool>());
        Assert.True(state["outputHeld"]!.GetValue<bool>());
        Assert.Null(state["otherApp"]);
        Assert.Null(state["importedFrom"]);
        Assert.False(state["autostart"]!["available"]!.GetValue<bool>());
        Assert.False(state["remoteAccess"]!["enabled"]!.GetValue<bool>());
        Assert.True(state["remoteAccess"]!["fixedByCommandLine"]!.GetValue<bool>());
        Assert.Empty(state["remoteAccess"]!["urls"]!.AsArray());
        Assert.False(state["passwordSet"]!.GetValue<bool>());
        Assert.Empty(state["acknowledged"]!.AsArray());

        var started = await client.CallAsync("setup", "start");
        Assert.Equal("setup-start", started["command"]!.GetValue<string>());
        Assert.True(started["info"]!["firstRunCompleted"]!.GetValue<bool>());
        Assert.False(started["info"]!["outputHeld"]!.GetValue<bool>());
        var update = client.NextEvent("setup-update");
        Assert.True(update["data"]!["firstRunCompleted"]!.GetValue<bool>());

        var acknowledged = await client.CallAsync("setup", "acknowledge", Obj("""{ "topic": "discovery" }"""));
        Assert.Equal(["discovery"], acknowledged["info"]!["acknowledged"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False((await client.CallAsync("setup", "acknowledge", Obj("""{ "topic": "firewall" }""")))["success"]!.GetValue<bool>());
        Assert.Equal("This copy of Overglow can't start with the system", (await client.CallAsync("setup", "autostart", Obj("""{ "enabled": true }""")))["error"]!.GetValue<string>());
        Assert.Equal("unknown or missing subcommand", (await client.CallAsync("setup", "nope"))["error"]!.GetValue<string>());

        Assert.True((await client.CallAsync("authorize", "newPassword", Obj("""{ "newPassword": "long-enough-1" }""")))["success"]!.GetValue<bool>());
        Assert.True(client.NextEvent("setup-update", e => e["data"]!["passwordSet"]!.GetValue<bool>())["data"]!["passwordSet"]!.GetValue<bool>());
    }

    [Fact]
    public async Task OtherDevicesSignInToStartTheLighting()
    {
        await using var server = await TestServer.StartAsync(firstRun: true, password: TestServer.Password);
        await using var client = await server.ConnectFromNetworkAsync();

        Assert.Equal("No Authorization", (await client.CallAsync("setup", "getstate"))["error"]!.GetValue<string>());
        Assert.Equal("No Authorization", (await client.CallAsync("setup", "start"))["error"]!.GetValue<string>());

        Assert.True((await client.CallAsync("authorize", "login", new JsonObject { ["password"] = TestServer.Password }))["success"]!.GetValue<bool>());
        Assert.True((await client.CallAsync("setup", "getstate"))["info"]!["passwordSet"]!.GetValue<bool>());
        Assert.True((await client.CallAsync("setup", "start"))["info"]!["firstRunCompleted"]!.GetValue<bool>());
    }
}
