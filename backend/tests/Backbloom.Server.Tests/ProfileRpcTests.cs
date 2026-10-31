using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Backbloom.Engine.Platform;
using Backbloom.Tests.Platform;

namespace Backbloom.Server.Tests;

/// <summary>The <c>profile</c> command, <c>serverinfo.profile</c> and the <c>profile-update</c> event.</summary>
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

        // chosen by hand
        var selected = await client.CallAsync("profile", "select", Obj("""{ "id": "game" }"""));
        Assert.True(selected["success"]!.GetValue<bool>(), selected.ToJsonString());
        Assert.Equal("""{"id":"game","name":"Game","manual":true,"game":false}""", selected["info"]!["profile"]!.ToJsonString());
        Assert.Equal("game", client.NextEvent("profile-update")["data"]!["profile"]!["id"]!.GetValue<string>());
        Assert.True((await client.CallAsync("serverinfo"))["info"]!["profile"]!["manual"]!.GetValue<bool>());
        Assert.Equal("game", (await client.CallAsync("profile", "getstate"))["info"]!["profile"]!["id"]!.GetValue<string>());

        // back to the programs: none runs
        Assert.Null((await client.CallAsync("profile", "select", Obj("""{ "id": null }""")))["info"]!["profile"]);
        Assert.Null(client.NextEvent("profile-update")["data"]!["profile"]);

        // the program starts: the profile follows within a poll (2 s)
        processes.Start("Game.exe");
        var update = client.NextEvent("profile-update");
        Assert.Equal("""{"id":"game","name":"Game","manual":false,"game":false}""", update["data"]!["profile"]!.ToJsonString());

        Assert.Equal("The layout has no profile 'nope'", (await client.CallAsync("profile", "select", Obj("""{ "id": "nope" }""")))["error"]!.GetValue<string>());
        Assert.False((await client.CallAsync("profile", "select", Obj("""{ "id": 5 }""")))["success"]!.GetValue<bool>());
        Assert.Equal("unknown or missing subcommand", (await client.CallAsync("profile"))["error"]!.GetValue<string>());
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
