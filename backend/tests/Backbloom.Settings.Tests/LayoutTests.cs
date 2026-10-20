using System.Text.Json.Nodes;
using Backbloom.Settings.Layout;
using static Backbloom.Settings.Tests.TestSchema;

namespace Backbloom.Settings.Tests;

public sealed class LayoutTests : IDisposable
{
    private static readonly string s_fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "layouts");

    private readonly TempDirectory _dir = new();
    private readonly List<OpenStore> _open = [];

    public void Dispose()
    {
        _open.ForEach(o => o.Dispose());
        _dir.Dispose();
    }

    private static JsonObject Spec() => Parse(File.ReadAllText(Path.Combine(s_fixtures, "ui-multi-controller.spec.json")));

    /// <summary>One controller with one output and one strip of <paramref name="count"/> LEDs along the top.</summary>
    private static JsonObject Single(string id = "c", int count = 3, string? device = null) => Parse($$"""
        { "version": 1,
          "controllers": [{ "id": "{{id}}", "name": "Desk", "outputs": [{ "index": 0, "start": 0, "length": {{count}} }] {{(device is null ? "" : $", \"device\": {device}")}} }],
          "strips": [{ "id": "s", "name": "Top", "controllerId": "{{id}}", "output": 0,
                       "segments": [{ "kind": "edge", "count": {{count}}, "edge": "top", "from": 0, "to": 1, "depth": 0.1 }] }] }
        """);

    private Storage.SqliteSettingsStore Open()
    {
        var store = OpenStore.In(_dir.Path);
        _open.Add(store);
        return store.Store;
    }

    private void CloseAll()
    {
        _open.ForEach(o => o.Dispose());
        _open.Clear();
    }

    private static List<SettingsError> Errors(JsonObject spec)
    {
        var errors = new List<SettingsError>();
        LayoutSpecification.Normalize(spec, Create(), (_, _) => null, errors);
        return errors;
    }

    [Fact]
    public void Compile_MatchesTheUiCompiler()
    {
        // ui-multi-controller.compiled.json is compileLayout() of the same spec (ui/src/layout/sharedFixture.test.ts)
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(s_fixtures, "ui-multi-controller.compiled.json")))!.AsArray();
        var errors = new List<SettingsError>();
        var spec = LayoutSpecification.Normalize(Spec(), Create(), (_, _) => null, errors);
        Assert.Empty(errors);

        var setup = LayoutSpecification.Compile(spec!);

        Assert.True(setup.FromLayout);
        Assert.Equal(expected.Select(c => c!["controllerId"]!.GetValue<string>()), setup.Controllers.Select(c => c.Id));
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i]!["leds"]!.ToJsonString(), setup.Controllers[i].Leds.ToJsonString());
        }

        Assert.Equal([168, 30, 25], setup.Controllers.Select(c => c.LedCount));
        Assert.Equal(223, setup.AllLeds().Count);
        var shelf = setup.Controllers[2];
        Assert.Equal(["shelf-top", "shelf-side"], shelf.Strips.Select(s => s.Id));
        Assert.Equal((10, 15, 0.5), (shelf.Strips[1].Start, shelf.Strips[1].Length, shelf.Strips[1].Brightness));
        Assert.Equal("grb", shelf.Device["colorOrder"]!.GetValue<string>());
    }

    [Fact]
    public void Normalize_RejectsUnknownControllersOutputsAndTooManyLeds()
    {
        var spec = Spec();
        spec["strips"]![0]!["controllerId"] = "nowhere";
        spec["strips"]![1]!["output"] = 7;
        spec["strips"]![2]!["segments"]![1]!["count"] = 3;
        spec["strips"]![4]!["output"] = 0;

        var errors = Errors(spec).Select(e => e.ToString()).ToList();

        Assert.Equal(5, errors.Count);
        Assert.Contains("layout.strips.4.segments: the strip has 12 LEDs, but its output drives only 10", errors);
        Assert.Contains(errors, e => e.StartsWith("layout.strips.0.controllerId: refers to the controller \"nowhere\"", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("layout.strips.1.output: refers to output 7", StringComparison.Ordinal));
        Assert.Contains("layout.strips.2.segments: the strip has 31 LEDs, but its output drives only 30", errors);
        Assert.Contains("layout.strips.4.output: output 0 of controller \"shelf\" already drives another strip", errors);
    }

    [Fact]
    public void Normalize_RequiresOutputsThatFollowEachOther()
    {
        var gap = Spec();
        gap["controllers"]![0]!["outputs"]![1]!["start"] = 95;
        var overlap = Spec();
        overlap["controllers"]![0]!["outputs"]![1]!["start"] = 90;
        var duplicate = Spec();
        duplicate["controllers"]![1]!["id"] = "wled-monitor-local";

        Assert.Contains("leaves LEDs 91..94 without an output", Assert.Single(Errors(gap)).Message, StringComparison.Ordinal);
        Assert.Contains("overlaps the previous output (LEDs 90..90", Assert.Single(Errors(overlap)).Message, StringComparison.Ordinal);
        Assert.Contains(Errors(duplicate), e => e.Path == "layout.controllers.1.id");
        Assert.Contains(Errors(Parse("""{ "version": 1, "controllers": [], "strips": [] }""")), e => e.Message == "needs at least one controller");
    }

    [Fact]
    public void Normalize_ValidatesEachControllersDriverSettings()
    {
        var spec = Spec();
        spec["controllers"]![1]!["device"] = Parse("""{ "type": "wled", "host": "desk.local", "brightnessMaxLevel": 999 }""");
        spec["controllers"]![2]!["device"] = Parse("""{ "type": "teleporter" }""");

        var errors = Errors(spec);

        Assert.Contains(errors, e => e.Path == "layout.controllers.1.device.brightnessMaxLevel");
        Assert.Contains(errors, e => e.Path == "layout.controllers.2.device.type" && e.Message.StartsWith("unknown LED device type", StringComparison.Ordinal));
    }

    [Fact]
    public void SavingALayout_DerivesTheLedsTheDeviceAndTheControllers()
    {
        var store = Open();
        var changed = new List<string>();
        store.Changed += c => changed.AddRange(c.Sections.Keys);

        var result = store.Update(new JsonObject { ["layout"] = Spec() });

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(["device", "layout", "leds"], changed.Order(StringComparer.Ordinal));
        Assert.Equal(223, store.GetSection("leds")!.AsArray().Count);
        Assert.Equal("a.txt", store.GetSection("device")!["output"]!.GetValue<string>());
        Assert.Equal(["Monitor and ceiling", "Desk", "Shelf"], store.Controllers.Select(c => c.Name));
        Assert.Equal([0, 1, 2], store.Controllers.Select(c => c.Id));
        Assert.Equal("c.txt", store.Controllers[2].Device["output"]!.GetValue<string>());

        // the driver defaults are filled in, and everything survives a restart
        CloseAll();
        var reopened = Open();
        Assert.Equal(223, reopened.GetSection("leds")!.AsArray().Count);
        Assert.Equal(3, reopened.Controllers.Count);
        Assert.Equal("wled", reopened.GetSection("layout")!["controllers"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal(3, LayoutSpecification.Compile(reopened.GetSection("layout")!.AsObject()).Controllers.Count);
    }

    [Fact]
    public void ALayoutWithoutDriverSettings_KeepsTheExistingDevice()
    {
        var store = Open();
        Assert.True(store.Update(Parse("""{ "device": { "type": "wled", "host": "tv.local" } }""")).Success);

        // first save from a client that doesn't send devices: the legacy controller's device is kept
        Assert.True(store.Update(new JsonObject { ["layout"] = Single("tv") }).Success);
        Assert.Equal("tv.local", store.GetSection("layout")!["controllers"]![0]!["device"]!["host"]!.GetValue<string>());
        Assert.Equal(255, store.GetSection("layout")!["controllers"]![0]!["device"]!["brightnessMaxLevel"]!.GetValue<int>());

        // later saves keep each controller's device by id; a new controller gets the default device
        var two = Single("tv");
        two["controllers"]!.AsArray().Add(Parse("""{ "id": "new", "outputs": [{ "index": 0, "start": 0, "length": 2 }] }"""));
        Assert.True(store.Update(new JsonObject { ["layout"] = two }).Success);
        Assert.Equal("tv.local", store.GetSection("device")!["host"]!.GetValue<string>());
        Assert.Equal("file", store.Controllers[1].Device["type"]!.GetValue<string>());
        Assert.Equal("new", store.Controllers[1].Name);
        Assert.Equal(5, store.GetSection("leds")!.AsArray().Count);
    }

    [Fact]
    public void SavingTheDevice_ChangesTheFirstControllerOfTheLayout()
    {
        var store = Open();
        Assert.True(store.Update(new JsonObject { ["layout"] = Spec() }).Success);

        Assert.True(store.Update(Parse("""{ "device": { "type": "file", "output": "other.txt", "printTimeStamp": false } }""")).Success);

        Assert.Equal("other.txt", store.GetSection("layout")!["controllers"]![0]!["device"]!["output"]!.GetValue<string>());
        Assert.Equal("other.txt", store.Controllers[0].Device["output"]!.GetValue<string>());
        Assert.Equal("b.txt", store.Controllers[1].Device["output"]!.GetValue<string>());
    }

    [Fact]
    public void SavingLeds_ReplacesASingleControllerLayout_AndIsRejectedForSeveralControllers()
    {
        var store = Open();
        Assert.True(store.Update(new JsonObject { ["layout"] = Single() }).Success);
        var unchanged = store.Update(new JsonObject { ["leds"] = store.GetSection("leds")! });
        Assert.True(unchanged.Success);
        Assert.NotNull(store.GetSection("layout"));

        // HyperHDR's LED editor saves leds: the layout no longer describes the LEDs and goes away
        Assert.True(store.Update(Parse("""{ "leds": [{ "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }] }""")).Success);
        Assert.Null(store.GetSection("layout"));
        Assert.Single(store.GetSection("leds")!.AsArray());
        CloseAll();
        Assert.Null(Open().GetSection("layout"));

        var multi = Open();
        Assert.True(multi.Update(new JsonObject { ["layout"] = Spec() }).Success);
        var rejected = multi.Update(Parse("""{ "leds": [{ "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }] }"""));
        Assert.Equal("leds", Assert.Single(rejected.Errors).Path);
        Assert.Equal(223, multi.GetSection("leds")!.AsArray().Count);
    }

    [Fact]
    public void AnInvalidLayout_IsRejectedAndChangesNothing()
    {
        var store = Open();
        var leds = store.GetSection("leds")!.ToJsonString();
        var spec = Spec();
        spec["strips"]![0]!["controllerId"] = "nowhere";

        var result = store.Update(new JsonObject { ["layout"] = spec, ["general"] = Parse("""{ "name": "x" }""") });

        Assert.False(result.Success);
        Assert.Null(store.GetSection("layout"));
        Assert.Equal(leds, store.GetSection("leds")!.ToJsonString());
        Assert.Equal("My Backbloom Config", store.GetSection("general")!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ALayoutStoredByAnOlderVersion_GetsTheControllersDeviceOnLoad()
    {
        var open = OpenStore.In(_dir.Path);
        _open.Add(open);
        Assert.True(open.Store.Update(Parse("""{ "device": { "type": "wled", "host": "tv.local" } }""")).Success);

        // the UI of port step 5 stored the layout as sent, without devices, next to the leds it compiled
        open.Database.Write((c, t) => Storage.SettingsDatabase.Execute(c, t,
            "INSERT INTO settings (section, value, updated_at) VALUES ('layout', $v, '')", ("$v", Single("tv").ToJsonString())));
        CloseAll();

        var store = Open();
        Assert.Equal("tv.local", store.GetSection("layout")!["controllers"]![0]!["device"]!["host"]!.GetValue<string>());
        Assert.Equal(3, store.GetSection("leds")!.AsArray().Count);
        Assert.Equal("Desk", Assert.Single(store.Controllers).Name);
    }

    [Fact]
    public void Setup_WithoutALayout_IsTheLegacyController()
    {
        var leds = JsonNode.Parse("""[{ "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }]""");

        var setup = ControllerSetup.FromSettings(null, leds, Parse("""{ "type": "file" }"""), "TV");

        var controller = Assert.Single(setup.Controllers);
        Assert.False(setup.FromLayout);
        Assert.Equal((ControllerSetup.LegacyId, "TV", 1, "file"), (controller.Id, controller.Name, controller.LedCount, controller.Device["type"]!.GetValue<string>()));
    }

    [Fact]
    public void Restore_DerivesTheControllersFromTheLayout()
    {
        var store = Open();
        Assert.True(store.Update(new JsonObject { ["layout"] = Spec() }).Success);
        var backup = store.Export();

        var target = OpenStore.In(Path.Combine(_dir.Path, "..", Path.GetFileName(_dir.Path) + "-b"));
        _open.Add(target);
        try
        {
            var result = target.Store.Replace(backup with { Controllers = [backup.Controllers[0]] });

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(3, target.Store.Controllers.Count);
            Assert.Equal(223, target.Store.GetSection("leds")!.AsArray().Count);
        }
        finally
        {
            target.Dispose();
            Directory.Delete(target.Database.DirectoryPath, recursive: true);
        }
    }
}
