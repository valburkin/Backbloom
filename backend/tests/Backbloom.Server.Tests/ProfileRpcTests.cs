using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Backbloom.Engine.Platform;
using Backbloom.Tests.Platform;

namespace Backbloom.Server.Tests;

/// <summary>The <c>profile</c> command, <c>serverinfo.profile</c> / <c>dismissedProfile</c> and the <c>profile-update</c> event.</summary>
public sealed class ProfileRpcTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    // One strip with a color zone; the profile "game" (game.exe) turns it blue.
    private const string Settings = """
        { "layout": { "version": 1,
            "room": { "width": 4, "depth": 4, "height": 2.6, "screen": { "x": 1.7, "y": 1.31, "width": 0.6, "height": 0.34 }, "furniture": "desk" },
            "controllers": [{ "id": "c", "name": "C", "outputs": [{ "index": 0, "start": 0, "length": 4 }],
                              "device": { "type": "file", "output": "\\\\.\\NUL", "printTimeStamp": false } }],
            "strips": [{ "id": "s", "name": "S", "controllerId": "c", "output": 0, "ledCount": 4,
                         "path": [{ "surface": "front", "x": 1.7, "y": 1.31, "next": { "kind": "color", "color": [255, 0, 0] } },
                                  { "surface": "front", "x": 2.3, "y": 1.31 }] }],
            "profiles": [{ "id": "game", "name": "Game", "apps": ["game.exe"], "sections": { "s": [{ "kind": "color", "color": [0, 0, 255] }] } }] } }
        """;

    private sealed class FakePrograms : IProgramList
    {
        public IReadOnlyList<RunningProgram> WindowedPrograms() => [new("game.exe", "The Game"), new("vlc.exe", "VLC media player")];
    }

    [Fact]
    public async Task AProfileIsChosenByHandOrByItsProgram_AndReported()
    {
        var processes = new FakeProcessList();
        await using var server = await TestServer.StartAsync(Settings, configure: (_, services) =>
        {
            services.AddSingleton<IProcessList>(processes);
            services.AddSingleton<IProgramList, FakePrograms>();
        });
        await using var client = await server.ConnectAsync();
        var info = (await client.CallAsync("serverinfo", null, Obj("""{ "subscribe": ["profile-update"] }""")))["info"]!.AsObject();
        Assert.True(info.ContainsKey("profile"));
        Assert.Null(info["profile"]);
        Assert.True(info.ContainsKey("dismissedProfile"));
        Assert.Null(info["dismissedProfile"]);
        Assert.False(info.ContainsKey("mode"));

        // chosen by hand
        var selected = await client.CallAsync("profile", "select", Obj("""{ "id": "game" }"""));
        Assert.True(selected["success"]!.GetValue<bool>(), selected.ToJsonString());
        Assert.Equal("""{"profile":{"id":"game","name":"Game","manual":true},"dismissed":null}""", selected["info"]!.ToJsonString());
        Assert.Equal("""{"profile":{"id":"game","name":"Game","manual":true},"dismissed":null}""", client.NextEvent("profile-update")["data"]!.ToJsonString());
        Assert.True((await client.CallAsync("serverinfo"))["info"]!["profile"]!["manual"]!.GetValue<bool>());
        Assert.Equal("game", (await client.CallAsync("profile", "getstate"))["info"]!["profile"]!["id"]!.GetValue<string>());
        Assert.Equal("""{"selected":"game"}""", (await client.CallAsync("config", "getconfig"))["info"]!["presets"]!.ToJsonString());

        // back to the programs: none runs
        Assert.Null((await client.CallAsync("profile", "select", Obj("""{ "id": null }""")))["info"]!["profile"]);
        Assert.Null(client.NextEvent("profile-update")["data"]!["profile"]);
        Assert.Equal("{}", (await client.CallAsync("config", "getconfig"))["info"]!["presets"]!.ToJsonString());

        // the program starts: the profile follows within a poll (2 s)
        processes.Start("Game.exe");
        var update = client.NextEvent("profile-update");
        Assert.Equal("""{"profile":{"id":"game","name":"Game","manual":false},"dismissed":null}""", update["data"]!.ToJsonString());

        // dismiss: Everyday until the program quits
        var dismissed = await client.CallAsync("profile", "dismiss");
        Assert.True(dismissed["success"]!.GetValue<bool>(), dismissed.ToJsonString());
        Assert.Equal("""{"profile":null,"dismissed":{"id":"game","name":"Game"}}""", dismissed["info"]!.ToJsonString());
        Assert.Equal("""{"profile":null,"dismissed":{"id":"game","name":"Game"}}""", client.NextEvent("profile-update")["data"]!.ToJsonString());
        Assert.Equal("""{"id":"game","name":"Game"}""", (await client.CallAsync("serverinfo"))["info"]!["dismissedProfile"]!.ToJsonString());
        Assert.Equal("""{"profile":null,"dismissed":{"id":"game","name":"Game"}}""", (await client.CallAsync("profile", "getstate"))["info"]!.ToJsonString());
        processes.Stop("Game.exe");
        Assert.Equal("""{"profile":null,"dismissed":null}""", client.NextEvent("profile-update")["data"]!.ToJsonString());

        Assert.Equal("The layout has no profile 'nope'", (await client.CallAsync("profile", "select", Obj("""{ "id": "nope" }""")))["error"]!.GetValue<string>());
        Assert.False((await client.CallAsync("profile", "select", Obj("""{ "id": 5 }""")))["success"]!.GetValue<bool>());
        Assert.Equal("unknown or missing subcommand", (await client.CallAsync("profile"))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task TurningAProfileOn_ClearsTheColorsClientsStarted()
    {
        await using var server = await TestServer.StartAsync(Settings);
        await using var client = await server.ConnectAsync();
        Assert.True((await client.CallAsync("color", null, Obj("""{ "color": [255, 0, 0], "priority": 1, "origin": "Home" }""")))["success"]!.GetValue<bool>());
        Assert.Contains(Priorities(await client.CallAsync("serverinfo")), p => p == 1);

        Assert.True((await client.CallAsync("profile", "select", Obj("""{ "id": "game" }""")))["success"]!.GetValue<bool>());
        Assert.DoesNotContain(Priorities(await client.CallAsync("serverinfo")), p => p == 1);

        static IEnumerable<int> Priorities(JsonObject reply) => reply["info"]!["priorities"]!.AsArray().Select(p => p!["priority"]!.GetValue<int>());
    }

    [Fact]
    public async Task ProgramsWithAWindow_AreListedForThePicker()
    {
        await using var server = await TestServer.StartAsync(Settings, configure: (_, services) => services.AddSingleton<IProgramList, FakePrograms>());
        await using var client = await server.ConnectAsync();

        var programs = (await client.CallAsync("profile", "programs"))["info"]!["programs"]!;

        Assert.Equal("""[{"exe":"game.exe","title":"The Game"},{"exe":"vlc.exe","title":"VLC media player"}]""", programs.ToJsonString());
    }
}
