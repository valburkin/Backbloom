using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Backbloom.Core.Smoothing;
using Backbloom.Devices;
using Backbloom.Engine.Platform;
using Backbloom.Platform.Windows;
using Backbloom.Settings;
using Backbloom.Settings.Storage;
using Backbloom.Tests.Platform;

namespace Backbloom.Engine.Tests;

/// <summary>Ambient and Game mode: chosen by hand, or Game while a game's profile runs (a fake process list and clock).</summary>
public sealed class ModeMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"backbloom-modes-{Guid.NewGuid():N}");
    private readonly List<IDisposable> _owned = [];
    private readonly FakeProcessList _processes = new();
    private readonly FakeTimeProvider _time = new();
    private SqliteSettingsStore? _store;

    public void Dispose()
    {
        _owned.ForEach(d => d.Dispose());
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ZoneTests.Layout's profile "game" (game.exe) marked as a game, and "video" (vlc.exe), which isn't one.
    private static JsonObject Layout()
    {
        var layout = JsonNode.Parse(ZoneTests.Layout())!.AsObject();
        var profiles = layout["profiles"]!.AsArray();
        profiles[0]!["game"] = true;
        profiles.Add(JsonNode.Parse("""{ "id": "video", "name": "Video", "apps": ["vlc.exe"], "game": false, "sections": {} }"""));
        return layout;
    }

    private (LightingEngine Engine, ProfileMonitor Profiles, ModeMonitor Modes) Start(string? mode = null)
    {
        var registry = new DriverRegistry().RegisterAll();
        var database = SettingsDatabase.Open(Path.Combine(_dir, SettingsDatabase.FileName));
        _owned.Add(database);
        _store = new SqliteSettingsStore(database, SettingsSchema.CreateBuiltin(registry.Drivers.ToDictionary(d => d.Type, d => d.Schema)), NullLogger<SqliteSettingsStore>.Instance);
        var sections = new JsonObject { ["layout"] = Layout() };
        if (mode is not null)
        {
            sections["modes"] = new JsonObject { ["mode"] = mode };
        }

        var result = _store.Update(sections);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        var devices = new DeviceManager(registry, new DriverContext(NullLoggerFactory.Instance, new HttpClient()), NullLoggerFactory.Instance);
        var engine = new LightingEngine(_store, new SolidSource(0, 255, 0), devices, new SystemClock(), NullLoggerFactory.Instance);
        _owned.Insert(0, engine);
        engine.Start();
        var profiles = new ProfileMonitor(engine, _store, _processes, _time, NullLogger<ProfileMonitor>.Instance);
        _owned.Insert(0, profiles);
        profiles.Start();
        var modes = new ModeMonitor(engine, _store, profiles, NullLogger<ModeMonitor>.Instance);
        _owned.Insert(0, modes);
        modes.Start();
        return (engine, profiles, modes);
    }

    private void Run(string program)
    {
        _processes.Start(program);
        _time.Advance(ProfileMonitor.PollInterval);
    }

    private void Quit(string program)
    {
        _processes.Stop(program);
        _time.Advance(ProfileMonitor.PollInterval);
    }

    [Fact]
    public void AmbientByDefault_GameModeTunesTransitionsAndColors_WithoutSavingThem()
    {
        var (engine, _, modes) = Start();
        Assert.Equal(new ModeState(LightMode.Ambient, LightMode.Ambient, null), modes.Current);
        Assert.False(engine.GameMode);
        Assert.Equal(1.0f, engine.Adjustment.SaturationGain);
        var ambientSmoothing = engine.SmoothingApplied.Config;

        Assert.True(modes.Set(LightMode.Game).Success);
        Assert.Equal(new ModeState(LightMode.Game, LightMode.Game, null), modes.Current);
        Assert.True(engine.GameMode);
        Assert.Equal(1.5f, engine.Adjustment.SaturationGain);
        Assert.Equal(1.2f, engine.Adjustment.LuminanceGain);
        Assert.Equal(new SmoothingConfig(false, 150, ambientSmoothing.UpdateIntervalMs, ambientSmoothing.Type, ambientSmoothing.SmoothingFactor, 817, 61, ambientSmoothing.YLimit), engine.SmoothingApplied.Config);
        Assert.True(engine.SmoothingApplied.Enable);

        // the saved sections stay as they were; only the mode is saved
        Assert.Equal(350, _store!.GetSection("smoothing")!["time_ms"]!.GetValue<int>());
        Assert.Equal(1.0, (_store.GetSection("color")!["channelAdjustment"] as JsonArray)![0]!["saturationGain"]!.GetValue<double>());
        Assert.Equal("game", _store.GetSection("modes")!["mode"]!.GetValue<string>());

        Assert.True(modes.Set(LightMode.Ambient).Success);
        Assert.False(engine.GameMode);
        Assert.Equal(1.0f, engine.Adjustment.SaturationGain);
        Assert.Equal(ambientSmoothing, engine.SmoothingApplied.Config);
    }

    [Fact]
    public void GameMode_ShowsOnlyTheScreen_AndAColorShowsAgainInAmbient()
    {
        var (engine, _, modes) = Start();
        engine.Clear(0); // the startup effect
        engine.SetColor(1, new Backbloom.Core.Color.Rgb8(255, 0, 0), 0, "UI");
        Assert.Equal(1, engine.Sources.Visible.Priority);

        Assert.True(modes.Set(LightMode.Game).Success);
        Assert.NotEqual(1, engine.Sources.Visible.Priority);
        Assert.Contains(engine.Sources.Priorities, p => p.Priority == 1 && p.Active && !p.Visible);

        Assert.True(modes.Set(LightMode.Ambient).Success);
        Assert.Equal(1, engine.Sources.Visible.Priority);
    }

    [Fact]
    public void ChangedGameValues_ApplyAtOnceInGameMode_AndSavedSettingsStillChangeUnderneath()
    {
        var (engine, _, modes) = Start(mode: "game");
        Assert.True(engine.GameMode);

        var section = _store!.GetSection("game")!.AsObject();
        section["saturationGain"] = 2.0;
        Assert.True(_store.Update(new JsonObject { ["game"] = section }).Success);
        Assert.Equal(2.0f, engine.Adjustment.SaturationGain);

        // another color setting changes: Game's gains stay on top of it
        var color = _store.GetSection("color")!.AsObject();
        color["channelAdjustment"]![0]!["gamma"] = 2.0;
        Assert.True(_store.Update(new JsonObject { ["color"] = color }).Success);
        Assert.Equal(2.0f, engine.Adjustment.SaturationGain);
        Assert.Equal(2.0f, engine.Adjustment.Gamma);
        Assert.Equal(LightMode.Game, modes.Current.Mode);
    }

    [Fact]
    public void AGamesProfile_TurnsGameModeOnWhileItRuns()
    {
        var (engine, _, modes) = Start();
        var changes = new List<ModeState>();
        modes.Changed += changes.Add;

        Run("vlc.exe");
        Assert.Equal(LightMode.Ambient, modes.Current.Mode);

        Quit("vlc.exe");
        Run("game.exe");
        Assert.Equal(new ModeState(LightMode.Game, LightMode.Ambient, "Game"), modes.Current);
        Assert.True(engine.GameMode);

        Quit("game.exe");
        Assert.Equal(new ModeState(LightMode.Ambient, LightMode.Ambient, null), modes.Current);
        Assert.False(engine.GameMode);
        Assert.Equal([LightMode.Game, LightMode.Ambient], changes.Select(c => c.Mode));
    }

    [Fact]
    public void ChoosingAmbientWhileAGameRuns_KeepsAmbientUntilAnotherProfileRuns()
    {
        var (_, profiles, modes) = Start(mode: "game");
        Run("game.exe");
        Assert.Equal(new ModeState(LightMode.Game, LightMode.Game, "Game"), modes.Current);

        Assert.True(modes.Set(LightMode.Ambient).Success);
        Assert.Equal(new ModeState(LightMode.Ambient, LightMode.Ambient, null, "Game"), modes.Current);
        _time.Advance(ProfileMonitor.PollInterval * 3);
        Assert.Equal(LightMode.Ambient, modes.Current.Mode);

        // the game quits and starts again: its profile turns Game mode on again
        Quit("game.exe");
        Assert.Null(profiles.Current);
        Run("game.exe");
        Assert.Equal(new ModeState(LightMode.Game, LightMode.Ambient, "Game"), modes.Current);
    }

    [Fact]
    public void ChoosingGameAgainWhileTheGameRuns_TurnsItBackOn_AndKeepsTheSavedMode()
    {
        var (_, _, modes) = Start();
        var changes = new List<ModeState>();
        modes.Changed += changes.Add;
        Run("game.exe");
        Assert.True(modes.Set(LightMode.Ambient).Success);
        Assert.Equal(new ModeState(LightMode.Ambient, LightMode.Ambient, null, "Game"), modes.Current);

        Assert.True(modes.Set(LightMode.Game).Success);
        Assert.Equal(new ModeState(LightMode.Game, LightMode.Ambient, "Game"), modes.Current);
        Assert.Equal("ambient", _store!.GetSection("modes")!["mode"]!.GetValue<string>());

        // the game quits: back to the saved Ambient
        Quit("game.exe");
        Assert.Equal(new ModeState(LightMode.Ambient, LightMode.Ambient, null), modes.Current);
        Assert.Equal([LightMode.Game, LightMode.Ambient, LightMode.Game, LightMode.Ambient], changes.Select(c => c.Mode));
    }

    [Fact]
    public void AnotherProfileInGameMode_KeepsGamesValues()
    {
        var (engine, _, modes) = Start(mode: "game");
        Run("vlc.exe");
        Assert.Equal(new ModeState(LightMode.Game, LightMode.Game, null), modes.Current);
        Assert.Equal("video", engine.ProfileId);
        Assert.Equal(1.5f, engine.Adjustment.SaturationGain);
        Assert.Equal(150, engine.SmoothingApplied.Config.SettlingTimeMs);
    }

    [Fact]
    public void MarkingTheRunningProfileAsAGame_SwitchesAtOnce()
    {
        var (_, _, modes) = Start();
        Run("vlc.exe");
        Assert.Equal(LightMode.Ambient, modes.Current.Mode);

        var layout = Layout();
        layout["profiles"]![1]!["game"] = true;
        Assert.True(_store!.Update(new JsonObject { ["layout"] = layout }).Success);
        Assert.Equal(new ModeState(LightMode.Game, LightMode.Ambient, "Video"), modes.Current);
    }
}
