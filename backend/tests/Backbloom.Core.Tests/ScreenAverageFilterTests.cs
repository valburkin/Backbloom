using System.Numerics;
using Backbloom.Core.Layout;
using Backbloom.Core.Smoothing;

namespace Backbloom.Core.Tests;

public sealed class ScreenAverageFilterTests
{
    // LED 0 and 2 show the whole screen, LED 1 an edge, LED 3 the whole screen in a group, LED 4 is off.
    private static readonly LedLayout s_layout = new([
        new LedArea(0, 1, 0, 1),
        new LedArea(0, 0.1, 0, 0.1),
        new LedArea(0, 1, 0, 1),
        new LedArea(0, 1, 0, 1, Group: 1),
        new LedArea(0, 1, 0, 1, Disabled: true),
    ]);

    [Fact]
    public void Layout_FindsTheWholeScreenLeds() => Assert.Equal([0, 2], s_layout.WholeScreenLeds);

    [Fact]
    public void Apply_FirstFramePassesUnchanged()
    {
        var filter = new ScreenAverageFilter();
        var colors = Frame(new Vector3(0.5f, 0.4f, 0.1f), Vector3.One);

        filter.Apply(s_layout, colors, 1000);

        Assert.Equal(Frame(new Vector3(0.5f, 0.4f, 0.1f), Vector3.One), colors);
    }

    [Fact]
    public void Apply_MovesTheWholeScreenLedsByTheElapsedTimeOnly()
    {
        var filter = new ScreenAverageFilter();
        filter.Apply(s_layout, Frame(Vector3.Zero, Vector3.Zero), 1000);
        var colors = Frame(Vector3.One, Vector3.One);

        filter.Apply(s_layout, colors, 1000 + (long)ScreenAverageFilter.TimeConstantMs);

        var expected = 1.0f - MathF.Exp(-1.0f);
        Assert.Equal(expected, colors[0].X, 1e-6f);
        Assert.Equal(colors[0], colors[2]);
        Assert.Equal(Vector3.One, colors[1]);
        Assert.Equal(Vector3.One, colors[3]);
        Assert.Equal(Vector3.One, colors[4]);
    }

    [Fact]
    public void Apply_SameResultAtAnyFrameRate()
    {
        var slow = new ScreenAverageFilter();
        var fast = new ScreenAverageFilter();
        slow.Apply(s_layout, Frame(Vector3.Zero, Vector3.Zero), 0);
        fast.Apply(s_layout, Frame(Vector3.Zero, Vector3.Zero), 0);

        var slowColors = Frame(Vector3.One, Vector3.One);
        slow.Apply(s_layout, slowColors, 400);
        var fastColors = Frame(Vector3.One, Vector3.One);
        for (var t = 20; t <= 400; t += 20)
        {
            fastColors = Frame(Vector3.One, Vector3.One);
            fast.Apply(s_layout, fastColors, t);
        }

        Assert.Equal(slowColors[0].X, fastColors[0].X, 1e-5f);
    }

    [Fact]
    public void Apply_SmallSwingsAreDamped()
    {
        // an olive average whose red swings by 5 levels every 33 ms frame: the output swings far less
        var filter = new ScreenAverageFilter();
        var low = new Vector3(85, 95, 60) / 255;
        var high = new Vector3(95, 95, 60) / 255;
        float min = 1, max = 0;
        for (var i = 0; i < 300; i++)
        {
            var colors = Frame(i % 2 == 0 ? low : high, Vector3.Zero);
            filter.Apply(s_layout, colors, i * 33L);
            if (i > 100)
            {
                min = MathF.Min(min, colors[0].X);
                max = MathF.Max(max, colors[0].X);
            }
        }

        Assert.True(max - min < 0.05f * (high.X - low.X), $"swing {max - min}");
    }

    [Fact]
    public void Apply_LayoutWithoutWholeScreenLedsIsLeftAlone()
    {
        var layout = new LedLayout([new LedArea(0, 0.5, 0, 1)]);
        var colors = new[] { new Vector3(0.3f) };

        new ScreenAverageFilter().Apply(layout, colors, 0);

        Assert.Equal(new Vector3(0.3f), colors[0]);
    }

    private static Vector3[] Frame(Vector3 whole, Vector3 other) => [whole, other, whole, other, other];
}
