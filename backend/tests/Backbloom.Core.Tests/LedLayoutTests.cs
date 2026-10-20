using System.Text.Json.Nodes;
using Backbloom.Core.Layout;

namespace Backbloom.Core.Tests;

public sealed class LedLayoutTests
{
    [Fact]
    public void FromJson_ReadsAreasGroupsAndDisabledFlag()
    {
        var layout = LedLayout.FromJson(JsonNode.Parse("""
            [
              { "hmin": 0.1, "hmax": 0.2, "vmin": 0.3, "vmax": 0.4, "group": 2 },
              { "hmin": 0.5, "hmax": 0.6, "vmin": 0.7, "vmax": 0.8, "disabled": true }
            ]
            """));

        Assert.Equal(2, layout.Count);
        Assert.Equal(new LedArea(0.1, 0.2, 0.3, 0.4, 2, false), layout.Areas[0]);
        Assert.Equal(new LedArea(0.5, 0.6, 0.7, 0.8, 0, true), layout.Areas[1]);
        Assert.True(layout.HasDisabled);
    }

    [Fact]
    public void FromJson_ClampsToUnitRangeAndSwapsReversedEdges()
    {
        var layout = LedLayout.FromJson(JsonNode.Parse("""[{ "hmin": 1.5, "hmax": -0.2, "vmin": 0.9, "vmax": 0.1 }]"""));

        Assert.Equal(new LedArea(0.0, 1.0, 0.1, 0.9), layout.Areas[0]);
    }

    [Fact]
    public void FromJson_MissingValuesReadAsZeroAndNonObjectsKeepTheirIndex()
    {
        var layout = LedLayout.FromJson(JsonNode.Parse("""[{ "hmax": 1 }, 42, { "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }]"""));

        Assert.Equal(3, layout.Count);
        Assert.Equal(new LedArea(0, 1, 0, 0), layout.Areas[0]);
        Assert.True(layout.Areas[0].IsEmpty);
        Assert.True(layout.Areas[1].IsEmpty);
        Assert.False(layout.Areas[2].IsEmpty);
        Assert.False(layout.HasDisabled);
    }

    [Fact]
    public void FromJson_GroupMustBeAnIntegerAndDisabledMustBeTrue()
    {
        // like QJsonValue::toInt(0) and toBool(false) in LedString::createLedString
        var layout = LedLayout.FromJson(JsonNode.Parse("""
            [{ "group": 2.5, "disabled": 1 }, { "group": "3" }, { "group": 4.0, "disabled": true }]
            """));

        Assert.Equal([0, 0, 4], layout.Areas.Select(a => a.Group));
        Assert.Equal([false, false, true], layout.Areas.Select(a => a.Disabled));
    }

    [Fact]
    public void FromJson_NonArrayIsEmpty()
    {
        Assert.Equal(0, LedLayout.FromJson(JsonNode.Parse("{}")).Count);
        Assert.Equal(0, LedLayout.FromJson(null).Count);
    }

    [Fact]
    public void FromJson_OwnersLayoutHas168Leds()
    {
        var layout = LedLayout.FromJson(Fixtures.Load("layouts/owner-168.json"));

        Assert.Equal(168, layout.Count);
        Assert.Equal(new LedArea(0.9694, 1, 0.8, 1), layout.Areas[0]);
        Assert.Equal(new LedArea(0, 0.039, 0, 0.25), layout.Areas[167]);
        Assert.All(layout.Areas, a => Assert.False(a.IsEmpty));
    }

    [Fact]
    public void GetGridSize_CountsDistinctCenters()
    {
        // Centers x: 100, 300, 500 (3 columns); y: 100, 900 (2 rows).
        var layout = new LedLayout(
        [
            new LedArea(0.0, 0.2, 0.0, 0.2),
            new LedArea(0.2, 0.4, 0.0, 0.2),
            new LedArea(0.4, 0.6, 0.0, 0.2),
            new LedArea(0.4, 0.6, 0.8, 1.0),
        ]);

        Assert.Equal((3, 2), layout.GetGridSize());
    }

    [Fact]
    public void GetGridSize_CorrectsAspectRatio()
    {
        // Five distinct columns on one row: height is raised to width / 2.
        var layout = new LedLayout(Enumerable.Range(0, 5).Select(i => new LedArea(i * 0.2, (i * 0.2) + 0.2, 0, 0.1)));

        Assert.Equal((5, 2), layout.GetGridSize());
    }

    [Fact]
    public void GetGridSize_LimitsTo80KeepingAspectRatio()
    {
        // 200 columns x 100 rows -> scaled into 80x80 as 80x40.
        var areas = new List<LedArea>();
        for (var i = 0; i < 200; i++)
        {
            areas.Add(new LedArea(i / 200.0, (i + 1) / 200.0, 0, 0.001));
        }

        for (var i = 0; i < 99; i++)
        {
            areas.Add(new LedArea(0, 0.005, (i + 1) / 100.0, (i + 1) / 100.0));
        }

        var layout = new LedLayout(areas);

        Assert.Equal((80, 40), layout.GetGridSize());
    }

    [Fact]
    public void GetGridSize_EmptyLayoutIsZero() => Assert.Equal((0, 0), LedLayout.Empty.GetGridSize());
}
