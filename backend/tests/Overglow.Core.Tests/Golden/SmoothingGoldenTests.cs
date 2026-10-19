namespace Overglow.Core.Tests.Golden;

/// <summary>
/// Smoothing fixtures: the six interpolators driven with an injected time line (C++: tests/golden/SmoothingGolden.cpp).
/// The interpolators are ported in port step 5 (after the owner compared them visually); until then these tests check
/// that the fixtures are complete and consistent.
/// </summary>
public sealed class SmoothingGoldenTests
{
    private const string NotPorted = "Smoothing (the interpolators of InfiniteSmoothing) is ported in port step 5.";

    public static TheoryData<string> SmoothingFixtures() => Fixtures.All("smoothing");

    [Theory]
    [MemberData(nameof(SmoothingFixtures))]
    public void Fixture_IsConsistent(string fixture)
    {
        var f = Fixtures.Load(fixture);
        var settings = f["settings"]!;
        var frequency = settings["updateFrequency"]?.GetValue<double>() ?? 25;

        // InfiniteSmoothing: round(max(1000 / max(updateFrequency, 20), 5)) ms between ticks
        var interval = f["updateInterval"]!.GetValue<long>();
        Assert.Equal((long)Math.Round(Math.Max(1000.0 / Math.Max(frequency, 20), 5.0)), interval);

        long previous = -1;
        long lastTick = -1;
        var ticks = 0;
        foreach (var step in f["steps"]!.AsArray())
        {
            var t = step!["t"]!.GetValue<long>();
            Assert.True(t >= previous, $"{fixture}: steps must be in time order (t = {t}).");
            previous = t;
            var colors = step["colors"] ?? step["target"];
            Assert.Equal(3, GoldenDiff.Vectors(colors).Length);
            if (step["colors"] is not null)
            {
                Assert.True(lastTick < 0 || t - lastTick == interval, $"{fixture}: tick at {t} after {lastTick}.");
                Assert.NotNull(step["complete"]);
                lastTick = t;
                ticks++;
            }
        }

        Assert.True(ticks * interval > 1000, $"{fixture}: only {ticks} ticks.");
    }

    [Fact(Skip = NotPorted)]
    public void Interpolators_MatchCppReference() => Assert.Fail(NotPorted);
}
