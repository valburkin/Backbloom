using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Backbloom.Core.Color;
using Backbloom.Core.Smoothing;
using Backbloom.Devices;
using Backbloom.Engine.Platform;
using Backbloom.Platform.Windows;
using Backbloom.Settings;
using Backbloom.Settings.Layout;
using Backbloom.Settings.Storage;
using Backbloom.Tests.Platform;

namespace Backbloom.Engine.Tests;

/// <summary>
/// Choosing the layout's profile (preset) by the running programs (a fake process list and clock) or by hand, turning it
/// off (dismiss), its Look and what turning it on clears.
/// </summary>
public sealed class ProfileMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"backbloom-profiles-{Guid.NewGuid():N}");
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

    // ZoneTests.Layout's profile "game" (game.exe), then "video" (vlc.exe, game.exe) and "empty" (no programs).
    private static JsonObject Layout()
    {
        var layout = JsonNode.Parse(ZoneTests.Layout())!.AsObject();
        var profiles = layout["profiles"]!.AsArray();
        profiles.Add(JsonNode.Parse("""{ "id": "video", "name": "Video", "apps": ["VLC.exe", "game.exe"], "sections": { "side": [null, { "kind": "color", "color": [9, 9, 9] }] } }"""));
        profiles.Add(JsonNode.Parse("""{ "id": "empty", "name": "Empty", "apps": [], "sections": {} }"""));
        return layout;
    }

    // The UI's Intense Look.
    private static readonly ProfileLook s_intense = new(150, 817, 61, 1.5, 1.2, 0);

    private static JsonObject LookJson(ProfileLook look) => new()
    {
        ["time_ms"] = look.TimeMs,
        ["stiffness"] = look.Stiffness,
        ["damping"] = look.Damping,
        ["saturationGain"] = look.SaturationGain,
        ["luminanceGain"] = look.LuminanceGain,
        ["backlightThreshold"] = look.BacklightThreshold,
    };

    // Layout() with the Intense Look on the profile "game".
    private static JsonObject LayoutWithLook()
    {
        var layout = Layout();
        layout["profiles"]![0]!["look"] = LookJson(s_intense);
        return layout;
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

    private ProfileMonitor NewMonitor(LightingEngine engine)
    {
        var monitor = new ProfileMonitor(engine, _store!, _processes, _time, NullLogger<ProfileMonitor>.Instance);
        _owned.Insert(0, monitor);
        monitor.Start();
        return monitor;
    }

    // The profile chosen by hand as saved (presets.selected).
    private string? Saved() => _store!.GetSection(ProfileMonitor.SectionName)!["selected"]?.GetValue<string>();

    private void Restart(ref ProfileMonitor monitor, LightingEngine engine)
    {
        _owned.Remove(monitor);
        monitor.Dispose();
        monitor = NewMonitor(engine);
    }

    private (LightingEngine Engine, ProfileMonitor Monitor) Start(JsonObject? layout = null, string? selected = null)
    {
        var registry = new DriverRegistry().RegisterAll();
        var database = SettingsDatabase.Open(Path.Combine(_dir, SettingsDatabase.FileName));
        _owned.Add(database);
        _store = new SqliteSettingsStore(database, SettingsSchema.CreateBuiltin(registry.Drivers.ToDictionary(d => d.Type, d => d.Schema)), NullLogger<SqliteSettingsStore>.Instance);
        var foreground = _store.GetSection("foregroundEffect")!.AsObject();
        foreground["enable"] = false; // no startup effect at priority 0
        var presets = selected is null ? new JsonObject() : new JsonObject { ["selected"] = selected };
        var result = _store.Update(new JsonObject { ["layout"] = layout ?? Layout(), ["foregroundEffect"] = foreground, [ProfileMonitor.SectionName] = presets });
        Assert.True(result.Success, string.Join("; ", result.Errors));
        var devices = new DeviceManager(registry, new DriverContext(NullLoggerFactory.Instance, new HttpClient()), NullLoggerFactory.Instance);
        var engine = new LightingEngine(_store, new SolidSource(0, 255, 0), devices, new SystemClock(), NullLoggerFactory.Instance);
        _owned.Insert(0, engine);
        engine.Start();
        return (engine, NewMonitor(engine));
    }

    [Fact]
    public void TheFirstProfileWhoseProgramRuns_IsApplied_AndTheLayoutComesBackWhenItQuits()
    {
        var (engine, monitor) = Start();
        var changes = new List<ProfileState>();
        monitor.Changed += changes.Add;
        Assert.Null(monitor.Current);
        Assert.Null(engine.ProfileId);

        _processes.Start("vlc.exe");
        _time.Advance(ProfileMonitor.PollInterval);
        Assert.Equal(new ActiveProfile("video", "Video", Manual: false), monitor.Current);
        Assert.Equal("video", engine.ProfileId);

        // both programs run: the first profile in the list wins
        _processes.Start("GAME.EXE");
        _time.Advance(ProfileMonitor.PollInterval);
        Assert.Equal("game", monitor.Current?.Id);
        Assert.Equal([ZoneRole.OfEffect("Rainbow swirl"), ZoneRole.OfColor(0, 0, 255)], engine.Zones.Roles);

        _processes.Stop("GAME.EXE");
        _processes.Stop("vlc.exe");
        _time.Advance(ProfileMonitor.PollInterval);
        Assert.Null(monitor.Current);
        Assert.Null(engine.ProfileId);
        Assert.Equal(["video", "game", null], changes.Select(c => c.Profile?.Id));
        Assert.All(changes, c => Assert.Null(c.Dismissed));
    }

    [Fact]
    public void AProfileChosenByHand_RunsUntilChoosingGoesBackToThePrograms()
    {
        var (engine, monitor) = Start();
        _processes.Start("vlc.exe");
        _time.Advance(ProfileMonitor.PollInterval);
        Assert.Equal("video", monitor.Current?.Id);

        Assert.True(monitor.Select("empty"));
        Assert.Equal(new ActiveProfile("empty", "Empty", Manual: true), monitor.Current);
        Assert.Equal("empty", engine.ProfileId);

        // no polling meanwhile
        var calls = _processes.Calls;
        _time.Advance(ProfileMonitor.PollInterval * 3);
        Assert.Equal(calls, _processes.Calls);
        Assert.Equal("empty", monitor.Current?.Id);

        Assert.False(monitor.Select("nope"));
        Assert.Equal("empty", monitor.Current?.Id);

        Assert.True(monitor.Select(null));
        Assert.Equal(new ActiveProfile("video", "Video", Manual: false), monitor.Current);
    }

    [Fact]
    public void ProfilesFollowTheLayout()
    {
        var (engine, monitor) = Start();
        Assert.True(monitor.Select("game"));

        // the chosen profile is removed from the layout: back to choosing by the programs (none runs)
        var layout = Layout();
        layout["profiles"]!.AsArray().RemoveAt(0);
        Assert.True(_store!.Update(new JsonObject { ["layout"] = layout.DeepClone() }).Success);
        Assert.Null(monitor.Current);
        Assert.Null(engine.ProfileId);

        // without profiles nothing is polled
        layout.Remove("profiles");
        Assert.True(_store.Update(new JsonObject { ["layout"] = layout.DeepClone() }).Success);
        var calls = _processes.Calls;
        _time.Advance(ProfileMonitor.PollInterval * 3);
        Assert.Equal(calls, _processes.Calls);
        Assert.False(monitor.Select("game"));
    }

    [Fact]
    public void ARunningProfile_IsAppliedAtStart_AndKeptWhenTheLayoutChanges()
    {
        _processes.Start("game.exe");
        var (engine, monitor) = Start();
        Assert.Equal("game", monitor.Current?.Id);
        Assert.Equal("game", engine.ProfileId);

        // the layout is saved again (another color for the side's zone): still compiled for the profile
        var layout = Layout();
        layout["profiles"]![0]!["sections"]!["side"]![0]!["color"] = new JsonArray(0, 255, 0);
        Assert.True(_store!.Update(new JsonObject { ["layout"] = layout.DeepClone() }).Success);
        Assert.Equal("game", engine.ProfileId);
        Assert.Equal(ZoneRole.OfColor(0, 255, 0), engine.Zones.Roles[1]);
    }

    [Fact]
    public void AProfilesLook_AppliesWhileItIsOn_OnTopOfTheSavedSettings_WithoutSavingThem()
    {
        var (engine, monitor) = Start(LayoutWithLook());
        var applied = new List<SmoothingSettings>();
        engine.SmoothingAppliedChanged += applied.Add;
        var saved = engine.SmoothingApplied;
        Assert.Null(engine.Look);
        Assert.Equal(350, saved.Config.SettlingTimeMs);
        Assert.Equal(1.0f, engine.Adjustment.SaturationGain);

        Run("game.exe");
        Assert.Equal("game", monitor.Current?.Id);
        Assert.Equal(s_intense, engine.Look);
        Assert.Equal(1.5f, engine.Adjustment.SaturationGain);
        Assert.Equal(1.2f, engine.Adjustment.LuminanceGain);
        Assert.Equal(0f, engine.Adjustment.BacklightThreshold);
        Assert.True(engine.SmoothingApplied.Enable);
        Assert.Equal(saved.Config with { SettlingTimeMs = 150, Stiffness = 817, Damping = 61 }, engine.SmoothingApplied.Config);
        Assert.Equal([engine.SmoothingApplied], applied);

        // the saved sections stay as they were
        Assert.Equal(350, _store!.GetSection("smoothing")!["time_ms"]!.GetValue<int>());
        Assert.Equal(1.0, _store.GetSection("color")!["channelAdjustment"]![0]!["saturationGain"]!.GetValue<double>());

        // another color setting changes underneath: the Look stays on top of it
        var color = _store.GetSection("color")!.AsObject();
        color["channelAdjustment"]![0]!["gamma"] = 2.0;
        Assert.True(_store.Update(new JsonObject { ["color"] = color }).Success);
        Assert.Equal(1.5f, engine.Adjustment.SaturationGain);
        Assert.Equal(2.0f, engine.Adjustment.Gamma);

        // the active profile's Look is edited: it applies at once
        var layout = LayoutWithLook();
        layout["profiles"]![0]!["look"] = LookJson(s_intense with { TimeMs = 200, SaturationGain = 1.25 });
        Assert.True(_store.Update(new JsonObject { ["layout"] = layout }).Success);
        Assert.Equal(1.25f, engine.Adjustment.SaturationGain);
        Assert.Equal(200, engine.SmoothingApplied.Config.SettlingTimeMs);

        // off again: the saved values
        Quit("game.exe");
        Assert.Null(monitor.Current);
        Assert.Null(engine.Look);
        Assert.Equal(1.0f, engine.Adjustment.SaturationGain);
        Assert.Equal(saved, engine.SmoothingApplied);
        Assert.Equal(saved, applied[^1]);
        Assert.Equal(3, applied.Count);
    }

    [Fact]
    public void AProfileWithoutALook_KeepsTheSavedOne()
    {
        var (engine, monitor) = Start(LayoutWithLook());
        var saved = engine.SmoothingApplied;
        Run("vlc.exe");
        Assert.Equal("video", monitor.Current?.Id);
        Assert.Null(engine.Look);
        Assert.Equal(saved, engine.SmoothingApplied);
        Assert.Equal(1.0f, engine.Adjustment.SaturationGain);
    }

    [Fact]
    public void TurningAProfileOn_ClearsTheColorsAndEffectsClientsStarted_ButNotTheStartupPriority()
    {
        var (engine, monitor) = Start();
        engine.SetColor(0, new Rgb8(1, 1, 1), 0, "Startup effect");
        engine.SetColor(1, new Rgb8(255, 0, 0), 0, "UI");
        engine.SetColor(100, new Rgb8(0, 255, 0), 0, "Home Assistant");

        Run("vlc.exe");
        Assert.Equal("video", monitor.Current?.Id);
        Assert.DoesNotContain(engine.Priorities, p => p.Priority is 1 or 100);
        Assert.Contains(engine.Priorities, p => p.Priority == 0);

        // a color started while the profile is on stays while it is on, also when it turns from by hand to by program
        engine.SetColor(1, new Rgb8(255, 0, 0), 0, "UI");
        Assert.True(monitor.Select("video"));
        Assert.True(monitor.Select(null));
        Assert.Equal(new ActiveProfile("video", "Video", Manual: false), monitor.Current);
        Assert.Contains(engine.Priorities, p => p.Priority == 1);

        // turning it off clears nothing; turning another one on does
        Assert.True(monitor.Select("empty"));
        Assert.DoesNotContain(engine.Priorities, p => p.Priority == 1);
        engine.SetColor(1, new Rgb8(255, 0, 0), 0, "UI");
        monitor.Dismiss();
        Assert.Equal("video", monitor.Current?.Id);
        Assert.DoesNotContain(engine.Priorities, p => p.Priority == 1);
        engine.SetColor(1, new Rgb8(255, 0, 0), 0, "UI");
        Quit("vlc.exe");
        Assert.Null(monitor.Current);
        Assert.Contains(engine.Priorities, p => p.Priority == 1);
    }

    [Fact]
    public void AProfileDismissedWhileItsProgramRuns_StaysOffUntilItQuits_AndTheNextRunTurnsItOnAgain()
    {
        var layout = Layout();
        layout["profiles"]!.AsArray().RemoveAt(1); // only "game" runs with game.exe
        var (engine, monitor) = Start(layout);
        var changes = new List<ProfileState>();
        monitor.Changed += changes.Add;
        Run("game.exe");
        Assert.Equal(new ProfileState(new ActiveProfile("game", "Game", Manual: false), null), monitor.State);

        Assert.Equal(new ProfileState(null, new DismissedProfile("game", "Game")), monitor.Dismiss());
        Assert.Null(engine.ProfileId);
        _time.Advance(ProfileMonitor.PollInterval * 3);
        Assert.Equal(new ProfileState(null, new DismissedProfile("game", "Game")), monitor.State);

        // back to choosing by the programs keeps the dismissal; dismissing again changes nothing
        Assert.True(monitor.Select(null));
        Assert.Equal(new ProfileState(null, new DismissedProfile("game", "Game")), monitor.Dismiss());

        // the program quits: the dismissal ends, and the next run turns the profile on again
        Quit("game.exe");
        Assert.Equal(ProfileState.None, monitor.State);
        Run("game.exe");
        Assert.Equal("game", monitor.Current?.Id);
        Assert.Equal("game", engine.ProfileId);
        Assert.Equal(["game", null, null, "game"], changes.Select(c => c.Profile?.Id));
        Assert.Equal([null, "game", null, null], changes.Select(c => c.Dismissed?.Id));
    }

    [Fact]
    public void ADismissedProfile_LetsTheNextRunningOneOn_AndADismissalEndsWhileAnotherIsChosenByHand()
    {
        var (_, monitor) = Start();
        Run("game.exe"); // "game", then "video" (vlc.exe, game.exe)
        Assert.Equal("game", monitor.Current?.Id);

        Assert.Equal(new ProfileState(new ActiveProfile("video", "Video", Manual: false), new DismissedProfile("game", "Game")), monitor.Dismiss());
        Assert.Equal(new ProfileState(null, new DismissedProfile("game", "Game")), monitor.Dismiss());

        // chosen by hand meanwhile: the programs are still watched for the dismissals
        Assert.True(monitor.Select("empty"));
        Assert.Equal(new ProfileState(new ActiveProfile("empty", "Empty", Manual: true), new DismissedProfile("game", "Game")), monitor.State);
        Quit("game.exe");
        Assert.Equal(new ProfileState(new ActiveProfile("empty", "Empty", Manual: true), null), monitor.State);

        // without dismissals nothing is polled while one is chosen by hand
        var calls = _processes.Calls;
        _time.Advance(ProfileMonitor.PollInterval * 3);
        Assert.Equal(calls, _processes.Calls);
    }

    [Fact]
    public void AProfileTurnedOnByHand_IsDismissedBackToThePrograms_AndStaysOffWhileItsOwnProgramRuns()
    {
        var layout = Layout();
        layout["profiles"]!.AsArray().RemoveAt(1);
        var (_, monitor) = Start(layout);
        Run("vlc.exe");

        // by hand, without programs of its own running: back to the programs (none of them runs)
        Assert.True(monitor.Select("empty"));
        Assert.Equal(ProfileState.None, monitor.Dismiss());

        // by hand while its own program runs: it doesn't come back as turned on by the program
        Run("game.exe");
        Assert.True(monitor.Select("game"));
        Assert.Equal(new ProfileState(null, new DismissedProfile("game", "Game")), monitor.Dismiss());

        // choosing it by hand again turns it on and ends the dismissal
        Assert.True(monitor.Select("game"));
        Assert.Equal(new ProfileState(new ActiveProfile("game", "Game", Manual: true), null), monitor.State);
        Assert.True(monitor.Select(null));
        Assert.Equal(new ProfileState(new ActiveProfile("game", "Game", Manual: false), null), monitor.State);

        // nothing on: dismiss changes nothing
        Quit("game.exe");
        Assert.Equal(ProfileState.None, monitor.Dismiss());
    }

    [Fact]
    public void ADismissal_EndsWithARestart_AndWithItsProfileRemoved()
    {
        var (engine, monitor) = Start();
        Run("game.exe");
        monitor.Dismiss();
        monitor.Dismiss();
        Assert.Equal(new DismissedProfile("game", "Game"), monitor.State.Dismissed);

        // the program restarts (a new monitor): the profile whose program runs is on
        _owned.Remove(monitor);
        monitor.Dispose();
        var restarted = NewMonitor(engine);
        Assert.Equal(new ProfileState(new ActiveProfile("game", "Game", Manual: false), null), restarted.State);

        // the dismissed profile is removed from the layout (renaming it shows the new name)
        restarted.Dismiss();
        var layout = Layout();
        layout["profiles"]![1]!["name"] = "Films";
        Assert.True(_store!.Update(new JsonObject { ["layout"] = layout.DeepClone() }).Success);
        Assert.Equal(new ProfileState(new ActiveProfile("video", "Films", Manual: false), new DismissedProfile("game", "Game")), restarted.State);
        layout["profiles"]!.AsArray().RemoveAt(0);
        Assert.True(_store.Update(new JsonObject { ["layout"] = layout.DeepClone() }).Success);
        Assert.Equal(new ProfileState(new ActiveProfile("video", "Films", Manual: false), null), restarted.State);
    }

    [Fact]
    public void AProfileChosenByHand_IsSaved_AndARestartRestoresIt()
    {
        var (engine, monitor) = Start();
        var saves = new List<JsonNode>();
        _store!.Changed += change =>
        {
            if (change.Sections.TryGetValue(ProfileMonitor.SectionName, out var presets))
            {
                saves.Add(presets);
            }
        };
        Assert.Null(Saved());

        // saved once: the monitor doesn't react to its own save
        Assert.True(monitor.Select("empty"));
        Assert.Equal("empty", Saved());
        Assert.Equal(["""{"selected":"empty"}"""], saves.Select(s => s.ToJsonString()));
        Assert.Equal(new ActiveProfile("empty", "Empty", Manual: true), monitor.Current);

        // the same choice again saves nothing
        Assert.True(monitor.Select("empty"));
        Assert.Single(saves);

        // the program restarts, also while a program of another profile runs
        _processes.Start("vlc.exe");
        Restart(ref monitor, engine);
        Assert.Equal(new ProfileState(new ActiveProfile("empty", "Empty", Manual: true), null), monitor.State);
        Assert.Equal("empty", engine.ProfileId);
        Assert.Single(saves);

        // nothing polled while it is chosen by hand
        var calls = _processes.Calls;
        _time.Advance(ProfileMonitor.PollInterval * 3);
        Assert.Equal(calls, _processes.Calls);
    }

    [Fact]
    public void TheSavedChoice_IsClearedByChoosingByTheProgramsAgain_ByDismissingIt_AndByRemovingItsProfile()
    {
        var (engine, monitor) = Start();

        // back to the programs
        Assert.True(monitor.Select("game"));
        Assert.Equal("game", Saved());
        Assert.True(monitor.Select(null));
        Assert.Null(Saved());

        // dismissed (Everyday)
        Assert.True(monitor.Select("game"));
        Assert.Equal(ProfileState.None, monitor.Dismiss());
        Assert.Null(Saved());

        // dismissing a profile turned on by its program saves nothing either
        Run("vlc.exe");
        Assert.Equal("video", monitor.Current?.Id);
        monitor.Dismiss();
        Assert.Null(Saved());
        Quit("vlc.exe");

        // removed from the layout
        Assert.True(monitor.Select("game"));
        var layout = Layout();
        layout["profiles"]!.AsArray().RemoveAt(0);
        Assert.True(_store!.Update(new JsonObject { ["layout"] = layout.DeepClone() }).Success);
        Assert.Null(monitor.Current);
        Assert.Null(Saved());

        // a restart chooses by the programs
        Restart(ref monitor, engine);
        Assert.Equal(ProfileState.None, monitor.State);
        Assert.Null(engine.ProfileId);
    }

    [Fact]
    public void ASavedProfileTheLayoutDoesntHave_IsClearedAtStart()
    {
        _processes.Start("vlc.exe");
        var (engine, monitor) = Start(selected: "gone");
        Assert.Null(Saved());
        Assert.Equal(new ActiveProfile("video", "Video", Manual: false), monitor.Current);
        Assert.Equal("video", engine.ProfileId);
    }

    [Fact]
    public void AChoiceSavedBySomeoneElse_IsFollowed()
    {
        var (engine, monitor) = Start();

        // e.g. a restored backup or setconfig
        Assert.True(_store!.Update(new JsonObject { [ProfileMonitor.SectionName] = new JsonObject { ["selected"] = "video" } }).Success);
        Assert.Equal(new ActiveProfile("video", "Video", Manual: true), monitor.Current);
        Assert.Equal("video", engine.ProfileId);

        // one the layout doesn't have: back to the programs, and cleared
        Assert.True(_store.Update(new JsonObject { [ProfileMonitor.SectionName] = new JsonObject { ["selected"] = "nope" } }).Success);
        Assert.Null(monitor.Current);
        Assert.Null(Saved());

        Assert.True(_store.Update(new JsonObject { [ProfileMonitor.SectionName] = new JsonObject { ["selected"] = "empty" } }).Success);
        Assert.Equal("empty", monitor.Current?.Id);
        Assert.True(_store.Update(new JsonObject { [ProfileMonitor.SectionName] = new JsonObject() }).Success);
        Assert.Null(monitor.Current);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void WindowsProgramList_ListsProgramsWithAWindow_OncePerExecutable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var programs = new WindowsProgramList().WindowedPrograms();

        // whatever runs on this desktop (nothing in a session without one): sorted, unique, never this test host
        Assert.Equal(programs.Select(p => p.Executable).Order(StringComparer.OrdinalIgnoreCase), programs.Select(p => p.Executable));
        Assert.Equal(programs.Count, programs.Select(p => p.Executable).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(programs, p => Assert.False(string.IsNullOrWhiteSpace(p.Title)));
        Assert.All(programs, p => Assert.EndsWith(".exe", p.Executable, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(new PortableProgramList().WindowedPrograms());
    }
}
