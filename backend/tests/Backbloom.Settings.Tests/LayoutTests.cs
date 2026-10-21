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

    // The UI's default room (room.ts defaultRoom): a 27" monitor on a desk in a 4 x 4 m room.
    private const string DeskRoom = """
        { "width": 4, "depth": 4, "height": 2.6, "screen": { "x": 1.7, "y": 1.31, "width": 0.6, "height": 0.34 }, "furniture": "desk" }
        """;

    /// <summary>One controller with one output and one strip of <paramref name="count"/> LEDs along the screen's top edge.</summary>
    private static JsonObject Single(string id = "c", int count = 3, string? device = null) => Parse($$"""
        { "version": 1, "room": {{DeskRoom}},
          "controllers": [{ "id": "{{id}}", "name": "Desk", "outputs": [{ "index": 0, "start": 0, "length": {{count}} }] {{(device is null ? "" : $", \"device\": {device}")}} }],
          "strips": [{ "id": "s", "name": "Top", "controllerId": "{{id}}", "output": 0, "ledCount": {{count}},
                       "path": [{ "surface": "front", "x": 1.7, "y": 1.31 }, { "surface": "front", "x": 2.3, "y": 1.31 }] }] }
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

    private static JsonArray Fixture(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(s_fixtures, name)))!.AsArray();

    private static JsonObject NormalizedSpec()
    {
        var errors = new List<SettingsError>();
        var spec = LayoutSpecification.Normalize(Spec(), Create(), (_, _) => null, errors);
        Assert.Empty(errors);
        return spec!;
    }

    // Within one step of the stored precision (4 decimals) per coordinate; the disabled LEDs and groups exactly.
    private static void AssertRects(JsonArray want, JsonArray got, string what)
    {
        Assert.Equal(want.Count, got.Count);
        for (var k = 0; k < want.Count; k++)
        {
            var (w, g) = (want[k]!.AsObject(), got[k]!.AsObject());
            var where = $"{what}, LED {k}: expected {w.ToJsonString()}, got {g.ToJsonString()}";
            Assert.True(w["disabled"]?.GetValue<bool>() == g["disabled"]?.GetValue<bool>(), where);
            Assert.Equal(w["group"]!.GetValue<int>(), g["group"]!.GetValue<int>());
            foreach (var key in (string[])["hmin", "hmax", "vmin", "vmax"])
            {
                Assert.True(Math.Abs(w[key]!.GetValue<double>() - g[key]!.GetValue<double>()) <= 1e-4 + 1e-12, where);
            }
        }
    }

    // compileLayout() and compileZones() of the UI (ui/src/layout/sharedFixture.test.ts)
    private static void AssertMatchesFixtures(ControllerSetup setup, string compiled, string zones)
    {
        var expected = Fixture(compiled);
        Assert.True(setup.FromLayout);
        Assert.Equal(expected.Select(c => c!["controllerId"]!.GetValue<string>()), setup.Controllers.Select(c => c.Id));
        for (var i = 0; i < expected.Count; i++)
        {
            AssertRects(expected[i]!["leds"]!.AsArray(), setup.Controllers[i].Leds, $"controller {i}");
        }

        // zones: the same roles in the same order, exactly the same LEDs, the rects within the precision
        var expectedZones = Fixture(zones);
        Assert.Equal(expectedZones.Count, setup.Zones.Count);
        for (var i = 0; i < expectedZones.Count; i++)
        {
            var (want, got) = (expectedZones[i]!.AsObject(), setup.Zones[i]);
            Assert.True(JsonNode.DeepEquals(want["role"], got.Role.ToJson()), $"zone {i}: expected {want["role"]!.ToJsonString()}, got {got.Role.ToJson().ToJsonString()}");
            Assert.Equal(want["leds"]!.AsArray().Select(n => n!.GetValue<int>()), got.Leds);
            AssertRects(want["rects"]!.AsArray(), got.Rects, $"zone {i}");
        }
    }

    [Fact]
    public void Compile_MatchesTheUiCompiler()
    {
        var setup = LayoutSpecification.Compile(NormalizedSpec());

        AssertMatchesFixtures(setup, "ui-multi-controller.compiled.json", "ui-multi-controller.zones.json");
        Assert.Null(setup.ProfileId);

        // the music effect on the ceiling and the desk strip, across two controllers; one color on the shelf
        Assert.Equal([ZoneRole.OfEffect("Music: fullscreen pulse (MULTI COLOR)"), ZoneRole.OfColor(255, 120, 0)], setup.Zones.Select(z => z.Role));
        Assert.Equal([168, 30, 25], setup.Controllers.Select(c => c.LedCount));
        Assert.Equal(223, setup.AllLeds().Count);
        var tv = setup.Controllers[0];
        Assert.Equal((91, 77, 0.8), (tv.Strips[1].Start, tv.Strips[1].Length, tv.Strips[1].Brightness));
        var shelf = setup.Controllers[2];
        Assert.Equal(["shelf-top", "shelf-side"], shelf.Strips.Select(s => s.Id));
        Assert.Equal((10, 15, 1.0), (shelf.Strips[1].Start, shelf.Strips[1].Length, shelf.Strips[1].Brightness));
        Assert.Equal("grb", shelf.Device["colorOrder"]!.GetValue<string>());
    }

    [Fact]
    public void CompileForAProfile_MatchesTheUiCompiler()
    {
        // the "witcher" profile: a candle on the monitor's first part and an auto section where the layout shows the
        // whole picture, a color on the ceiling (its second entry null: auto), and on the desk strip "none", a color
        // that stays a gap (the strip's gap) and the whole picture
        var spec = NormalizedSpec();
        var setup = ControllerSetup.FromSettings(spec, null, null, "unused", "witcher");

        AssertMatchesFixtures(setup, "ui-multi-controller.witcher.compiled.json", "ui-multi-controller.witcher.zones.json");
        Assert.Equal("witcher", setup.ProfileId);
        Assert.Equal([ZoneRole.OfEffect("Candle"), ZoneRole.OfColor(0, 40, 80), ZoneRole.OfColor(255, 120, 0)], setup.Zones.Select(z => z.Role));

        // the saved layout stays as it is
        Assert.Equal(Spec()["strips"]!.ToJsonString(), spec["strips"]!.ToJsonString());
    }

    [Fact]
    public void ApplyProfile_IgnoresUnknownProfilesAndListsMadeForAnotherShape()
    {
        var spec = NormalizedSpec();

        // "movies" has one entry for the ceiling's two sections: ignored, so it compiles like the layout
        Assert.Same(spec, LayoutSpecification.ApplyProfile(spec, null));
        Assert.Same(spec, LayoutSpecification.ApplyProfile(spec, "nope"));
        Assert.Equal(spec.ToJsonString(), LayoutSpecification.ApplyProfile(spec, "movies").ToJsonString());
        AssertMatchesFixtures(LayoutSpecification.Compile(spec, "movies"), "ui-multi-controller.compiled.json", "ui-multi-controller.zones.json");
        Assert.Null(LayoutSpecification.Compile(spec, "nope").ProfileId);

        Assert.Equal(
            [("witcher", "Witcher 3", "witcher3.exe"), ("movies", "Movies", "vlc.exe,mpc-hc64.exe")],
            LayoutSpecification.Profiles(spec).Select(p => (p.Id, p.Name, string.Join(',', p.Apps))));
        Assert.Empty(LayoutSpecification.Profiles(Single()));
    }

    [Fact]
    public void Zones_LeaveOutDarkLedsAndLedsPastTheirOutput_AndKeepColorsApart()
    {
        // two strips of 4 on one controller; the first plays an effect from LED 1 on (LED 0 is dark), the second shows a
        // color on its first part and the same effect on its second
        var spec = Parse($$"""
            { "version": 1, "room": {{DeskRoom}},
              "controllers": [{ "id": "c", "outputs": [{ "index": 0, "start": 0, "length": 4 }, { "index": 1, "start": 4, "length": 4 }] }],
              "strips": [
                { "id": "a", "controllerId": "c", "output": 0, "ledCount": 4, "dark": [{ "start": 0, "count": 1 }],
                  "path": [{ "surface": "front", "x": 1.7, "y": 1.31, "next": { "kind": "effect", "effect": "Candle" } }, { "surface": "front", "x": 2.3, "y": 1.31 }] },
                { "id": "b", "controllerId": "c", "output": 1, "ledCount": 4,
                  "path": [{ "surface": "front", "x": 1.7, "y": 1.31, "next": { "kind": "color", "color": [1, 2, 3] } },
                           { "surface": "front", "x": 2.0, "y": 1.31, "next": { "kind": "effect", "effect": "Candle" } },
                           { "surface": "front", "x": 2.3, "y": 1.31 }] }] }
            """);
        Assert.Empty(Errors(spec));

        var setup = LayoutSpecification.Compile(spec);

        Assert.Equal([(ZoneRole.OfEffect("Candle"), "1,2,3,6,7"), (ZoneRole.OfColor(1, 2, 3), "4,5")], setup.Zones.Select(z => (z.Role, string.Join(',', z.Leds))));

        // zone LEDs leave the screen mapping; their zone rects are where 'auto' would put them (the top edge)
        Assert.All(setup.Controllers[0].Leds, r => Assert.True(r!["disabled"]!.GetValue<bool>()));
        Assert.Equal("""{"hmin":0.225,"hmax":0.525,"vmin":0,"vmax":0.1,"group":0}""", setup.Zones[0].Rects[0]!.ToJsonString());
        var zoneRects = LayoutSpecification.CompileStrip(spec["strips"]![1]!.AsObject(), spec["room"]!.AsObject(), zone: true);
        Assert.Equal(
            """[{"hmin":0,"hmax":0.275,"vmin":0,"vmax":0.1,"group":0},{"hmin":0.225,"hmax":0.525,"vmin":0,"vmax":0.1,"group":0},""" +
            """{"hmin":0.475,"hmax":0.775,"vmin":0,"vmax":0.1,"group":0},{"hmin":0.725,"hmax":1,"vmin":0,"vmax":0.1,"group":0}]""",
            new JsonArray([.. zoneRects]).ToJsonString());
    }

    [Fact]
    public void CompileStrip_AStripAlongTheScreensTopEdge_SamplesTheTopOfThePicture()
    {
        var spec = Single();
        var strip = spec["strips"]![0]!.AsObject();
        var room = spec["room"]!.AsObject();

        // the section faces the top edge from corner to corner (auto): LED k of 3 sits at (k + 0.5) / 3 along it,
        // each 1.2 LED spacings wide
        Assert.Equal(
            """[{"hmin":0,"hmax":0.3667,"vmin":0,"vmax":0.1,"group":0},{"hmin":0.3,"hmax":0.7,"vmin":0,"vmax":0.1,"group":0},{"hmin":0.6333,"hmax":1,"vmin":0,"vmax":0.1,"group":0}]""",
            Compile(strip, room));

        // dark LEDs, and a section showing one area of the picture (cut to fit)
        strip["dark"] = JsonNode.Parse("""[{ "start": 1, "count": 1 }]""");
        strip["path"]![0]!["next"] = JsonNode.Parse("""{ "kind": "area", "area": { "hmin": 0.2, "hmax": 0.4, "vmin": 0, "vmax": 1.5 } }""");
        Assert.Equal(
            """[{"hmin":0.2,"hmax":0.4,"vmin":0,"vmax":1,"group":0},{"hmin":0.5,"hmax":0.5,"vmin":0.5,"vmax":0.5,"group":0,"disabled":true},{"hmin":0.2,"hmax":0.4,"vmin":0,"vmax":1,"group":0}]""",
            Compile(strip, room));

        // the whole picture; nothing
        strip.Remove("dark");
        strip["path"]![0]!["next"] = JsonNode.Parse("""{ "kind": "whole" }""");
        Assert.Equal(
            """[{"hmin":0,"hmax":1,"vmin":0,"vmax":1,"group":0},{"hmin":0,"hmax":1,"vmin":0,"vmax":1,"group":0},{"hmin":0,"hmax":1,"vmin":0,"vmax":1,"group":0}]""",
            Compile(strip, room));
        strip["path"]![0]!["next"] = JsonNode.Parse("""{ "kind": "none" }""");
        Assert.All(LayoutSpecification.CompileStrip(strip, room), r => Assert.True(r["disabled"]!.GetValue<bool>()));
    }

    [Fact]
    public void CompileStrip_SpreadsTheLedsOverTheSectionsWithLeds_AndEachRepeatsItsStretch()
    {
        var spec = Single(count: 4);
        var strip = spec["strips"]![0]!.AsObject();
        var room = spec["room"]!.AsObject();

        // along the top edge (left to right), a gap down the right side (no LEDs, no length), then the bottom edge
        // from right to left: two LEDs per section, each section's stretch shared by its LEDs
        strip["path"] = JsonNode.Parse("""
            [{ "surface": "front", "x": 1.7, "y": 1.31, "next": { "kind": "edge", "from": 0, "to": 1 } },
             { "surface": "front", "x": 2.3, "y": 1.31, "next": { "kind": "gap" } },
             { "surface": "front", "x": 2.3, "y": 1.65, "next": { "kind": "edge", "from": 2, "to": 3 } },
             { "surface": "front", "x": 1.7, "y": 1.65 }]
            """);

        Assert.Equal(
            """[{"hmin":0,"hmax":0.55,"vmin":0,"vmax":0.1,"group":0},{"hmin":0.45,"hmax":1,"vmin":0,"vmax":0.1,"group":0},""" +
            """{"hmin":0.45,"hmax":1,"vmin":0.9,"vmax":1,"group":0},{"hmin":0,"hmax":0.55,"vmin":0.9,"vmax":1,"group":0}]""",
            Compile(strip, room));

        // a stretch crossing the top-left corner (3.5 to 4.5): up the left edge, then along the top
        strip["path"] = JsonNode.Parse("""
            [{ "surface": "front", "x": 1.7, "y": 1.31, "next": { "kind": "edge", "from": 3.5, "to": 4.5 } },
             { "surface": "front", "x": 2.3, "y": 1.31 }]
            """);
        Assert.Equal(
            """[{"hmin":0,"hmax":0.0567,"vmin":0.225,"vmax":0.525,"group":0},{"hmin":0,"hmax":0.0567,"vmin":0,"vmax":0.275,"group":0},""" +
            """{"hmin":0,"hmax":0.275,"vmin":0,"vmax":0.1,"group":0},{"hmin":0.225,"hmax":0.525,"vmin":0,"vmax":0.1,"group":0}]""",
            Compile(strip, room));
    }

    [Fact]
    public void CompileStrip_AnLedAtTheEndOfAGapGoesToTheSectionAfterIt()
    {
        var spec = Single(count: 2);
        var strip = spec["strips"]![0]!.AsObject();
        var room = spec["room"]!.AsObject();

        // the strip starts with a gap whose ends are both at LED position 0.5, where LED 0 sits (placeLeds moves on
        // past empty pieces that end at an LED)
        strip["path"] = JsonNode.Parse("""
            [{ "surface": "front", "x": 1.7, "y": 1.0, "at": 0.5, "next": { "kind": "gap" } },
             { "surface": "front", "x": 1.7, "y": 1.31, "at": 0.5 },
             { "surface": "front", "x": 2.3, "y": 1.31 }]
            """);

        Assert.Equal(
            """[{"hmin":0,"hmax":0.3,"vmin":0,"vmax":0.1,"group":0},{"hmin":0.3667,"hmax":0.9667,"vmin":0,"vmax":0.1,"group":0}]""",
            Compile(strip, room));
    }

    [Fact]
    public void AutoSections_RepeatTheStretchOfTheOutlineTheyFace()
    {
        var spec = Single(count: 2);
        var strip = spec["strips"]![0]!.AsObject();
        var room = spec["room"]!.AsObject();

        // across the ceiling from wall to wall (seen straight on: above the screen): the whole top edge
        strip["path"] = JsonNode.Parse("""[{ "surface": "ceiling", "x": 0, "y": 2 }, { "surface": "ceiling", "x": 4, "y": 2 }]""");
        Assert.Equal(
            """[{"hmin":0,"hmax":0.55,"vmin":0,"vmax":0.1,"group":0},{"hmin":0.45,"hmax":1,"vmin":0,"vmax":0.1,"group":0}]""",
            Compile(strip, room));

        // down the screen's right side from inside it (out to the closest edge): the right edge, top to bottom
        strip["path"] = JsonNode.Parse("""[{ "surface": "front", "x": 2.29, "y": 1.31 }, { "surface": "front", "x": 2.29, "y": 1.65 }]""");
        Assert.Equal(
            """[{"hmin":0.9433,"hmax":1,"vmin":0,"vmax":0.5517,"group":0},{"hmin":0.9433,"hmax":1,"vmin":0.4483,"vmax":1,"group":0}]""",
            Compile(strip, room));

        // the short way round: from below the bottom edge (2.75 on the outline) to the left of the top-left corner (0,
        // which is 4): round the bottom-left corner and up the left edge
        strip["path"] = JsonNode.Parse("""[{ "surface": "floor", "x": 1.85, "y": 0 }, { "surface": "front", "x": 0.5, "y": 1.31 }]""");
        Assert.Equal(
            """[{"hmin":0,"hmax":0.0567,"vmin":0.5625,"vmax":1,"group":0},{"hmin":0,"hmax":0.0567,"vmin":0,"vmax":0.6875,"group":0}]""",
            Compile(strip, room));
    }

    private static string Compile(JsonObject strip, JsonObject room) =>
        new JsonArray([.. LayoutSpecification.CompileStrip(strip, room)]).ToJsonString();

    [Fact]
    public void Normalize_AcceptsAValidLayout_AndKeepsWhatItDoesntUse()
    {
        var spec = Spec();
        spec["room"]!["note"] = "kept";
        spec["strips"]![0]!["path"]![0]!["label"] = "start";

        // what the previous model had (never released): a seat, and strips that show the screen or an area
        spec["room"]!["seat"] = Parse("""{ "x": 2, "y": 0.75, "eyeHeight": 1.2 }""");
        spec["strips"]![1]!["show"] = "wall";

        // the last point's section is ignored
        spec["strips"]![1]!["path"]![2]!["next"] = "anything";

        var errors = new List<SettingsError>();
        var normalized = LayoutSpecification.Normalize(spec, Create(), (_, _) => null, errors);

        Assert.Empty(errors);
        Assert.Equal("kept", normalized!["room"]!["note"]!.GetValue<string>());
        Assert.Equal(2, normalized["room"]!["seat"]!["x"]!.GetValue<int>());
        Assert.Equal("start", normalized["strips"]![0]!["path"]![0]!["label"]!.GetValue<string>());
        Assert.Equal(18, normalized["strips"]![0]!["path"]![1]!["at"]!.GetValue<int>());
    }

    [Fact]
    public void Normalize_RejectsUnknownControllersOutputsAndTooManyLeds()
    {
        var spec = Spec();
        spec["strips"]![0]!["controllerId"] = "nowhere";
        spec["strips"]![1]!["output"] = 7;
        spec["strips"]![2]!["ledCount"] = 31;
        spec["strips"]![3]!["ledCount"] = 11;
        spec["strips"]![4]!["output"] = 0;

        var errors = Errors(spec).Select(e => e.ToString()).ToList();

        Assert.Equal(5, errors.Count);
        Assert.Contains(errors, e => e.StartsWith("layout.strips.0.controllerId: refers to the controller \"nowhere\"", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("layout.strips.1.output: refers to output 7", StringComparison.Ordinal));
        Assert.Contains("layout.strips.2.ledCount: the strip has 31 LEDs, but its output drives only 30", errors);
        Assert.Contains("layout.strips.3.ledCount: the strip has 11 LEDs, but its output drives only 10", errors);
        Assert.Contains("layout.strips.4.output: output 0 of controller \"shelf\" already drives another strip", errors);
    }

    // Strip 1 is the ceiling strip (77 LEDs, two points on a 4.2 x 3.6 m ceiling), strip 0 the monitor strip
    // (91 LEDs).
    [Theory]
    [InlineData(1, "ledCount", "0", "layout.strips.1.ledCount")]
    [InlineData(1, "ledCount", "2.5", "layout.strips.1.ledCount")]
    [InlineData(1, "path", """[{ "surface": "ceiling", "x": 1, "y": 1 }]""", "layout.strips.1.path")]
    [InlineData(1, "path", """{ "surface": "ceiling", "x": 1, "y": 1 }""", "layout.strips.1.path")]
    [InlineData(1, "path", """[{ "surface": "wall", "x": 1, "y": 1 }, { "surface": "ceiling", "x": 2, "y": 1 }]""", "layout.strips.1.path.0")]
    [InlineData(1, "path", """[{ "surface": "ceiling", "x": 1 }, { "surface": "ceiling", "x": 2, "y": 1 }]""", "layout.strips.1.path.0")]
    [InlineData(1, "path", """[{ "surface": "ceiling", "x": 1, "y": 1 }, { "surface": "ceiling", "x": 4.5, "y": 1 }]""", "layout.strips.1.path.1")]
    [InlineData(1, "path", """[{ "surface": "ceiling", "x": 1, "y": 1, "at": -1 }, { "surface": "ceiling", "x": 2, "y": 1 }]""", "layout.strips.1.path.0.at")]
    [InlineData(1, "path", """[{ "surface": "ceiling", "x": 1, "y": 1 }, { "surface": "ceiling", "x": 2, "y": 1, "at": 78 }]""", "layout.strips.1.path.1.at")]
    [InlineData(1, "path", """[{ "surface": "ceiling", "x": 1, "y": 1 }, { "surface": "ceiling", "x": 2, "y": 1, "at": null }]""", "layout.strips.1.path.1.at")]
    [InlineData(1, "path", """[{ "surface": "ceiling", "x": 1, "y": 1 }, { "surface": "ceiling", "x": 1, "y": 1 }]""", "layout.strips.1.path")]
    [InlineData(1, "path", """[{ "surface": "ceiling", "x": 1, "y": 1 }, { "surface": "ceiling", "x": 2, "y": 1, "at": 50 }, { "surface": "ceiling", "x": 3, "y": 1, "at": 40 }]""", "layout.strips.1.path.2")]
    [InlineData(1, "path", """[{ "surface": "ceiling", "x": 1, "y": 1, "next": { "kind": "gap" } }, { "surface": "ceiling", "x": 2, "y": 1 }]""", "layout.strips.1.path")]
    [InlineData(0, "dark", """[{ "start": 90, "count": 2 }]""", "layout.strips.0.dark.0")]
    [InlineData(0, "dark", """[{ "start": 0, "count": 0 }]""", "layout.strips.0.dark.0")]
    [InlineData(0, "dark", """{ "start": 0, "count": 1 }""", "layout.strips.0.dark")]
    [InlineData(1, "depth", "0", "layout.strips.1.depth")]
    [InlineData(1, "depth", "1.5", "layout.strips.1.depth")]
    [InlineData(1, "blend", "0", "layout.strips.1.blend")]
    [InlineData(1, "blend", "\"wide\"", "layout.strips.1.blend")]
    [InlineData(1, "brightness", "1.2", "layout.strips.1.brightness")]
    public void Normalize_RejectsAnInvalidStrip(int strip, string property, string json, string path)
    {
        var spec = Spec();
        spec["strips"]![strip]![property] = JsonNode.Parse(json);

        Assert.Equal(path, Assert.Single(Errors(spec)).Path);
    }

    // The sections of the desk strip (strip 2: front wall, gap, edge 3.9 -> 3.1, auto); point 0 starts the first.
    [Theory]
    [InlineData("""{ "kind": "wave" }""", "layout.strips.2.path.0.next")]
    [InlineData("""{ "from": 0, "to": 1 }""", "layout.strips.2.path.0.next")]
    [InlineData("\"gap\"", "layout.strips.2.path.0.next")]
    [InlineData("null", "layout.strips.2.path.0.next")]
    [InlineData("""{ "kind": "edge", "from": 0 }""", "layout.strips.2.path.0.next")]
    [InlineData("""{ "kind": "edge", "from": 0, "to": "1" }""", "layout.strips.2.path.0.next")]
    [InlineData("""{ "kind": "area" }""", "layout.strips.2.path.0.next.area")]
    [InlineData("""{ "kind": "area", "area": { "hmin": 0, "hmax": 1, "vmin": 0 } }""", "layout.strips.2.path.0.next.area")]
    [InlineData("""{ "kind": "area", "area": { "hmin": 1, "hmax": 0, "vmin": 0, "vmax": 1 } }""", "layout.strips.2.path.0.next.area")]
    [InlineData("""{ "kind": "area", "area": { "hmin": 0, "hmax": 1, "vmin": 0.6, "vmax": 0.5 } }""", "layout.strips.2.path.0.next.area")]
    [InlineData("""{ "kind": "effect" }""", "layout.strips.2.path.0.next.effect")]
    [InlineData("""{ "kind": "effect", "effect": " " }""", "layout.strips.2.path.0.next.effect")]
    [InlineData("""{ "kind": "effect", "effect": 7 }""", "layout.strips.2.path.0.next.effect")]
    [InlineData("""{ "kind": "color" }""", "layout.strips.2.path.0.next.color")]
    [InlineData("""{ "kind": "color", "color": [255, 0] }""", "layout.strips.2.path.0.next.color")]
    [InlineData("""{ "kind": "color", "color": [255, 0, 256] }""", "layout.strips.2.path.0.next.color")]
    [InlineData("""{ "kind": "color", "color": [255, 0, 1.5] }""", "layout.strips.2.path.0.next.color")]
    [InlineData("""{ "kind": "color", "color": [255, 0, -1] }""", "layout.strips.2.path.0.next.color")]
    [InlineData("""{ "kind": "color", "color": "#ff0000" }""", "layout.strips.2.path.0.next.color")]
    public void Normalize_RejectsAnInvalidSection(string json, string path)
    {
        var spec = Spec();
        spec["strips"]![2]!["path"]![0]!["next"] = JsonNode.Parse(json);

        Assert.Equal(path, Assert.Single(Errors(spec)).Path);
    }

    [Fact]
    public void Normalize_AcceptsEverySection_AndAreasReachingOutsideThePicture()
    {
        foreach (var json in (string[])[
            """{ "kind": "edge", "from": -2, "to": 7.5 }""", """{ "kind": "whole" }""", """{ "kind": "none" }""", """{ "kind": "gap" }""",
            """{ "kind": "area", "area": { "hmin": -0.5, "hmax": 1.5, "vmin": 0, "vmax": 1 } }""",
            """{ "kind": "effect", "effect": "Candle" }""", """{ "kind": "effect", "effect": "Not in the catalog" }""",
            """{ "kind": "color", "color": [0, 128, 255] }"""])
        {
            var spec = Spec();
            spec["strips"]![2]!["path"]![0]!["next"] = JsonNode.Parse(json);

            Assert.Empty(Errors(spec));
        }
    }

    // Profile 0 of the fixture is "witcher" (its sections for the ceiling, the monitor and the desk strip).
    [Theory]
    [InlineData("""{ "profiles": {} }""", "layout.profiles")]
    [InlineData("""{ "profiles": null }""", "layout.profiles")]
    [InlineData("""{ "profiles": ["witcher"] }""", "layout.profiles.0.id")]
    [InlineData("""{ "id": " " }""", "layout.profiles.0.id")]
    [InlineData("""{ "id": 3 }""", "layout.profiles.0.id")]
    [InlineData("""{ "id": "movies" }""", "layout.profiles.1.id")]
    [InlineData("""{ "name": "" }""", "layout.profiles.0.name")]
    [InlineData("""{ "name": null }""", "layout.profiles.0.name")]
    [InlineData("""{ "apps": "witcher3.exe" }""", "layout.profiles.0.apps")]
    [InlineData("""{ "apps": ["witcher3.exe", ""] }""", "layout.profiles.0.apps")]
    [InlineData("""{ "apps": [3] }""", "layout.profiles.0.apps")]
    [InlineData("""{ "sections": [] }""", "layout.profiles.0.sections")]
    [InlineData("""{ "sections": { "ceiling": { "kind": "none" } } }""", "layout.profiles.0.sections.ceiling")]
    [InlineData("""{ "sections": { "ceiling": [{ "kind": "gap" }, null] } }""", "layout.profiles.0.sections.ceiling.0")]
    [InlineData("""{ "sections": { "ceiling": [null, { "kind": "wave" }] } }""", "layout.profiles.0.sections.ceiling.1")]
    [InlineData("""{ "sections": { "ceiling": [null, 4] } }""", "layout.profiles.0.sections.ceiling.1")]
    [InlineData("""{ "sections": { "ceiling": [{ "kind": "color", "color": [0, 0] }, null] } }""", "layout.profiles.0.sections.ceiling.0.color")]
    [InlineData("""{ "sections": { "nowhere": [{ "kind": "effect", "effect": "" }] } }""", "layout.profiles.0.sections.nowhere.0.effect")]
    [InlineData("""{ "sections": { "ceiling": [{ "kind": "edge", "from": 0 }] } }""", "layout.profiles.0.sections.ceiling.0")]
    public void Normalize_RejectsAnInvalidProfile(string json, string path)
    {
        var spec = Spec();
        var change = Parse(json);
        if (change.ContainsKey("profiles"))
        {
            spec["profiles"] = change["profiles"]?.DeepClone();
        }
        else
        {
            foreach (var (key, value) in change)
            {
                spec["profiles"]![0]![key] = value?.DeepClone();
            }
        }

        Assert.Equal(path, Assert.Single(Errors(spec)).Path);
    }

    [Fact]
    public void Normalize_AcceptsProfiles_ThatChangeStripsThatArentThere_OrHaveNoPrograms()
    {
        // the UI warns about both; applied, such a list is ignored
        var spec = Spec();
        spec["profiles"]![0]!["apps"] = new JsonArray();
        spec["profiles"]![0]!["sections"]!["gone"] = JsonNode.Parse("""[{ "kind": "none" }]""");
        spec["profiles"]![1]!["sections"]!["ceiling"] = JsonNode.Parse("""[{ "kind": "none" }, null, null]""");
        Assert.Empty(Errors(spec));

        spec.Remove("profiles");
        Assert.Empty(Errors(spec));
        spec["profiles"] = new JsonArray();
        Assert.Empty(Errors(spec));
    }

    [Theory]
    [InlineData(null, "null", "layout.room")]
    [InlineData("width", "0.2", "layout.room.width")]
    [InlineData("depth", "60", "layout.room.depth")]
    [InlineData("height", "\"high\"", "layout.room.height")]
    [InlineData("screen", """{ "x": 1.8, "y": 1.3, "width": 0, "height": 0.34 }""", "layout.room.screen")]
    [InlineData("screen", """{ "x": 3.8, "y": 1.3, "width": 0.6, "height": 0.34 }""", "layout.room.screen")]
    [InlineData("screen", """{ "x": 1.8, "y": 1.3, "width": 0.6 }""", "layout.room.screen")]
    [InlineData("screen", """{ "x": 1.8, "y": 2.4, "width": 0.6, "height": 0.34 }""", "layout.room.screen")]
    [InlineData("furniture", "\"sofa\"", "layout.room.furniture")]
    [InlineData("furniture", "null", "layout.room.furniture")]
    public void Normalize_RejectsAnInvalidRoom(string? property, string json, string path)
    {
        var spec = Spec();
        if (property is null)
        {
            spec.Remove("room");
        }
        else
        {
            spec["room"]![property] = JsonNode.Parse(json);
        }

        // the strips' paths aren't measured against a room that is wrong itself
        Assert.Equal(path, Assert.Single(Errors(spec)).Path);
    }

    [Fact]
    public void Normalize_ChecksThePathsAgainstTheRoom()
    {
        var spec = Spec();

        // the ceiling strip starts 4.05 m from the left wall
        spec["room"]!["width"] = 3;

        Assert.Equal("layout.strips.1.path.0", Assert.Single(Errors(spec)).Path);
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
    public void ALayoutStoredInTheSegmentFormat_IsDroppedOnLoad_AndTheLedsStay()
    {
        var open = OpenStore.In(_dir.Path);
        _open.Add(open);
        var leds = open.Store.GetSection("leds")!.ToJsonString();

        // strips made of screen-edge segments, before the room model (never released): no compatibility code
        open.Database.Write((c, t) => Storage.SettingsDatabase.Execute(c, t,
            "INSERT INTO settings (section, value, updated_at) VALUES ('layout', $v, '')", ("$v", """
                { "version": 1, "controllers": [{ "id": "c", "outputs": [{ "index": 0, "start": 0, "length": 3 }] }],
                  "strips": [{ "id": "s", "controllerId": "c", "output": 0,
                               "segments": [{ "kind": "edge", "count": 3, "edge": "top", "from": 0, "to": 1, "depth": 0.1 }] }] }
                """)));
        CloseAll();

        var store = Open();
        Assert.Null(store.GetSection("layout"));
        Assert.Equal(leds, store.GetSection("leds")!.ToJsonString());
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
