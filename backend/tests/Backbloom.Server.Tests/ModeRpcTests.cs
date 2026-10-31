using System.Text.Json.Nodes;

namespace Backbloom.Server.Tests;

/// <summary>The <c>mode</c> command, <c>serverinfo.mode</c> and the <c>mode-update</c> event.</summary>
public sealed class ModeRpcTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    // One strip; the profile "game" (game.exe) is a game's.
    private const string Settings = """
        { "layout": { "version": 1,
            "room": { "width": 4, "depth": 4, "height": 2.6, "screen": { "x": 1.7, "y": 1.31, "width": 0.6, "height": 0.34 }, "furniture": "desk" },
            "controllers": [{ "id": "c", "name": "C", "outputs": [{ "index": 0, "start": 0, "length": 4 }],
                              "device": { "type": "file", "output": "\\\\.\\NUL", "printTimeStamp": false } }],
            "strips": [{ "id": "s", "name": "S", "controllerId": "c", "output": 0, "ledCount": 4,
                         "path": [{ "surface": "front", "x": 1.7, "y": 1.31 }, { "surface": "front", "x": 2.3, "y": 1.31 }] }],
            "profiles": [{ "id": "game", "name": "My game", "apps": ["game.exe"], "game": true, "sections": {} }] } }
        """;

    [Fact]
    public async Task TheModeIsChosen_OrTurnedOnByAGamesProfile_AndReported()
    {
        await using var server = await TestServer.StartAsync(Settings);
        await using var client = await server.ConnectAsync();
        var info = (await client.CallAsync("serverinfo", null, Obj("""{ "subscribe": ["mode-update"] }""")))["info"]!.AsObject();
        Assert.Equal("""{"mode":"ambient","saved":"ambient","gameProfile":null,"dismissedGame":null}""", info["mode"]!.ToJsonString());

        var set = await client.CallAsync("mode", "set", Obj("""{ "mode": "game" }"""));
        Assert.True(set["success"]!.GetValue<bool>(), set.ToJsonString());
        Assert.Equal("""{"mode":"game","saved":"game","gameProfile":null,"dismissedGame":null}""", set["info"]!["mode"]!.ToJsonString());
        Assert.Equal("game", client.NextEvent("mode-update")["data"]!["mode"]!["mode"]!.GetValue<string>());
        Assert.Equal("game", (await client.CallAsync("config", "getconfig"))["info"]!["modes"]!["mode"]!.GetValue<string>());

        Assert.True((await client.CallAsync("mode", "set", Obj("""{ "mode": "ambient" }""")))["success"]!.GetValue<bool>());
        Assert.Equal("ambient", client.NextEvent("mode-update")["data"]!["mode"]!["mode"]!.GetValue<string>());

        // the game's profile (here previewed) turns Game mode on
        Assert.True((await client.CallAsync("profile", "select", Obj("""{ "id": "game" }""")))["success"]!.GetValue<bool>());
        Assert.Equal("""{"mode":"game","saved":"ambient","gameProfile":"My game","dismissedGame":null}""", client.NextEvent("mode-update")["data"]!["mode"]!.ToJsonString());
        Assert.Equal("My game", (await client.CallAsync("mode", "getstate"))["info"]!["mode"]!["gameProfile"]!.GetValue<string>());

        Assert.Equal("mode must be \"ambient\" or \"game\"", (await client.CallAsync("mode", "set", Obj("""{ "mode": "party" }""")))["error"]!.GetValue<string>());
        Assert.Equal("unknown or missing subcommand", (await client.CallAsync("mode"))["error"]!.GetValue<string>());
    }
}
