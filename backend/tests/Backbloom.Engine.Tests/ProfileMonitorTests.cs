using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Backbloom.Devices;
using Backbloom.Engine.Platform;
using Backbloom.Platform.Windows;
using Backbloom.Settings;
using Backbloom.Settings.Layout;
using Backbloom.Settings.Storage;
using Backbloom.Tests.Platform;

namespace Backbloom.Engine.Tests;

/// <summary>Choosing the layout's profile by the running programs (a fake process list and clock) or by hand.</summary>
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

    private (LightingEngine Engine, ProfileMonitor Monitor) Start(JsonObject? layout = null)
    {
        var registry = new DriverRegistry().RegisterAll();
        var database = SettingsDatabase.Open(Path.Combine(_dir, SettingsDatabase.FileName));
        _owned.Add(database);
        _store = new SqliteSettingsStore(database, SettingsSchema.CreateBuiltin(registry.Drivers.ToDictionary(d => d.Type, d => d.Schema)), NullLogger<SqliteSettingsStore>.Instance);
        var result = _store.Update(new JsonObject { ["layout"] = layout ?? Layout() });
        Assert.True(result.Success, string.Join("; ", result.Errors));
        var devices = new DeviceManager(registry, new DriverContext(NullLoggerFactory.Instance, new HttpClient()), NullLoggerFactory.Instance);
        var engine = new LightingEngine(_store, new SolidSource(0, 255, 0), devices, new SystemClock(), NullLoggerFactory.Instance);
        _owned.Insert(0, engine);
        engine.Start();
        var monitor = new ProfileMonitor(engine, _store, _processes, _time, NullLogger<ProfileMonitor>.Instance);
        _owned.Insert(0, monitor);
        monitor.Start();
        return (engine, monitor);
    }

    [Fact]
    public void TheFirstProfileWhoseProgramRuns_IsApplied_AndTheLayoutComesBackWhenItQuits()
    {
        var (engine, monitor) = Start();
        var changes = new List<ActiveProfile?>();
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
        Assert.Equal(["video", "game", null], changes.Select(c => c?.Id));
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
