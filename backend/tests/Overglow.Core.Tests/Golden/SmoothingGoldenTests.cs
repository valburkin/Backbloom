using System.Numerics;
using Overglow.Core.Smoothing;

namespace Overglow.Core.Tests.Golden;

/// <summary>
/// Smoothing fixtures: the six interpolators driven with an injected time line (C++: tests/golden/SmoothingGolden.cpp):
/// frames at about 60 Hz set the target, ticks at the update interval advance and read the colors.
/// </summary>
/// <remarks>
/// Exact match: the same float operations in the same order, <c>powf</c>/<c>expf</c> of the same C runtime, and the
/// BT.709 inverse matrix computed in float the way linalg does.
/// </remarks>
public sealed class SmoothingGoldenTests
{
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

    [Theory]
    [MemberData(nameof(SmoothingFixtures))]
    public void Interpolator_MatchesCppReference(string fixture)
    {
        var f = Fixtures.Load(fixture);
        var config = SmoothingConfig.FromSettings(f["settings"]);
        Assert.Equal(f["settlingTime"]!.GetValue<long>(), config.SettlingTimeMs);
        Assert.Equal(f["updateInterval"]!.GetValue<long>(), config.UpdateIntervalMs);
        var interpolator = Interpolator.Create(config);
        var minimalBacklight = f["minimalBacklight"]!.GetValue<float>();
        var diff = new GoldenDiff(fixture, f["tolerance"]!.GetValue<float>());
        var output = new Vector3[8];

        foreach (var step in f["steps"]!.AsArray())
        {
            var t = step!["t"]!.GetValue<long>();
            if (step["target"] is { } target)
            {
                interpolator.SetTargetColors(GoldenDiff.Vectors(target), t);
                continue;
            }

            interpolator.UpdateCurrentColors(t, minimalBacklight);
            var count = interpolator.GetCurrentColors(output, minimalBacklight);
            var expected = GoldenDiff.Vectors(step["colors"]);
            diff.Equal($"t={t} complete", step["complete"]!.GetValue<bool>(), interpolator.IsAnimationComplete);
            diff.Equal($"t={t} count", expected.Length, count);
            for (var i = 0; i < Math.Min(expected.Length, count); i++)
            {
                diff.Compare($"t={t} LED {i}", expected[i], output[i]);
            }
        }

        diff.AssertNoMismatches();
    }
}
