using System.Numerics;
using System.Text.Json.Nodes;
using Overglow.Core.Smoothing;

namespace Overglow.Core.Tests;

[Collection(AllocationMeasurements.Name)]
public sealed class SmootherTests
{
    private static readonly Vector3[] s_black = [Vector3.Zero, Vector3.Zero];
    private static readonly Vector3[] s_white = [Vector3.One, Vector3.One];

    private static Smoother Create(string settings)
    {
        var smoother = new Smoother();
        smoother.Configure(SmoothingSettings.FromJson(JsonNode.Parse(settings)));
        return smoother;
    }

    [Fact]
    public void Settings_UseTheCppFallbacksAndInterval()
    {
        var s = SmoothingSettings.FromJson(new JsonObject());
        Assert.True(s.Enable && s.ContinuousOutput && s.AntiFlickeringFilter);
        Assert.Equal(new SmoothingConfig(false, 200, 40, SmoothingType.Stepper, 0.1f, 200, 26, 0), s.Config);

        Assert.Equal(20, SmoothingConfig.IntervalFromFrequency(50));
        Assert.Equal(50, SmoothingConfig.IntervalFromFrequency(10)); // at least 20 Hz
        Assert.Equal(5, SmoothingConfig.IntervalFromFrequency(200));
        Assert.Equal(7, SmoothingConfig.IntervalFromFrequency(150)); // 6.67 rounds up
    }

    [Fact]
    public void Tick_SendsTheTransitionThenAFewMoreFramesThenOnePerSecond()
    {
        var smoother = Create("""{ "enable": true, "type": "RgbInterpolator", "time_ms": 100, "updateFrequency": 50, "continuousOutput": false, "smoothingFactor": 0 }""");
        var output = new Vector3[2];
        smoother.SetTarget(s_black, 0, 0);
        smoother.SetTarget(s_white, 0, 10);

        var sent = new List<long>();
        for (long t = 20; t <= 2500; t += 20)
        {
            if (smoother.Tick(t, output) > 0)
            {
                sent.Add(t);
            }
        }

        // the 100 ms transition (until the tick that finds the target reached), three cool-down frames, then one
        // keep-alive per second
        Assert.Equal([20, 40, 60, 80, 100, 120, 140, 160, 180], sent.Take(9));
        Assert.Equal(Vector3.One, output[0]);
        Assert.Equal([1180, 2180], sent.Skip(9));
    }

    [Fact]
    public void ContinuousOutput_SendsEveryTick()
    {
        var smoother = Create("""{ "enable": true, "type": "Stepper", "time_ms": 100, "continuousOutput": true }""");
        var output = new Vector3[2];
        smoother.SetTarget(s_white, 0, 0);
        Assert.All(Enumerable.Range(1, 100), i => Assert.Equal(2, smoother.Tick(i * 20, output)));
    }

    [Fact]
    public void Disabled_OrPausedConfig_SendsNothing()
    {
        var smoother = Create("""{ "enable": false }""");
        var output = new Vector3[2];
        smoother.SetTarget(s_white, 0, 0);
        Assert.False(smoother.IsActive);
        Assert.Equal(0, smoother.UpdateIntervalMs);
        Assert.Equal(0, smoother.Tick(20, output));

        smoother.SetEnabled(true);
        var direct = smoother.AddCustomConfig(-1, 0, 25, pause: true);
        Assert.Equal(Smoother.EffectConfigsStart, direct);
        smoother.UseConfig(direct);
        Assert.False(smoother.IsActive);

        smoother.UseConfig(Smoother.UserConfig);
        Assert.True(smoother.IsActive);
        Assert.Equal(40, smoother.UpdateIntervalMs);
    }

    [Fact]
    public void CustomConfigs_AreReusedAndUnknownIdsSelectTheUserConfig()
    {
        var smoother = Create("""{ "enable": true, "type": "HybridRgbInterpolator", "time_ms": 150, "updateFrequency": 50 }""");
        var a = smoother.AddCustomConfig(-1, 300, 25, pause: false);
        var b = smoother.AddCustomConfig(-1, 300, 25, pause: false);
        var c = smoother.AddCustomConfig(-1, 500, 50, pause: false);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);

        Assert.True(smoother.SelectConfig(c));
        Assert.IsType<StepperInterpolator>(smoother.Interpolator);
        Assert.Equal(20, smoother.UpdateIntervalMs);

        Assert.False(smoother.SelectConfig(99));
        Assert.Equal(Smoother.UserConfig, smoother.CurrentConfigId);
        Assert.IsType<HybridRgbInterpolator>(smoother.Interpolator);
    }

    [Fact]
    public void UseConfig_DoesNotRestartTheCurrentOne()
    {
        var smoother = Create("""{ "enable": true, "type": "YuvInterpolator" }""");
        var interpolator = smoother.Interpolator;
        smoother.UseConfig(Smoother.UserConfig);
        Assert.Same(interpolator, smoother.Interpolator);
    }

    [Fact]
    public void EveryInterpolator_ReachesTheTarget()
    {
        foreach (var type in SmoothingTypes.All)
        {
            var interpolator = Interpolator.Create(SmoothingConfig.FromSettings(JsonNode.Parse($$"""{ "type": "{{type.ToSettingString()}}", "time_ms": 150, "y_limit": 0.05, "stiffness": 150, "damping": 26, "smoothingFactor": 0 }""")));
            interpolator.SetTargetColors(s_black, 0);
            interpolator.SetTargetColors([new Vector3(1, 0, 0), new Vector3(0.2f, 0.4f, 0.6f)], 1);
            var output = new Vector3[2];
            for (long t = 20; t < 5000 && !interpolator.IsAnimationComplete; t += 20)
            {
                interpolator.UpdateCurrentColors(t, 0);
            }

            Assert.True(interpolator.IsAnimationComplete, type.ToString());
            Assert.Equal(2, interpolator.GetCurrentColors(output, 0));
            Assert.True(Vector3.Distance(new Vector3(1, 0, 0), output[0]) < 0.001f, $"{type}: {output[0]}");
        }
    }

    [Fact]
    public void SmoothingTicks_DoNotAllocate()
    {
        foreach (var type in SmoothingTypes.All)
        {
            var smoother = Create($$"""{ "enable": true, "type": "{{type.ToSettingString()}}", "continuousOutput": true }""");
            var colors = new Vector3[168];
            var output = new Vector3[168];
            void Frame(long t)
            {
                colors[t % 168] = new Vector3((t % 7) / 7.0f, 0.5f, 1 - ((t % 5) / 5.0f));
                smoother.SetTarget(colors, 0.0039f, t);
                smoother.Tick(t + 10, output);
            }

            // Warm up past tiered-JIT promotion so recompilation cannot allocate inside the measured window.
            var allocated = Allocations.Measure(
                () =>
                {
                    for (long t = 0; t < 2_000; t += 20)
                    {
                        Frame(t);
                    }
                },
                () =>
                {
                    for (long t = 2_000; t < 22_000; t += 20)
                    {
                        Frame(t);
                    }
                });
            Assert.Equal(0, allocated);
        }
    }

    [Fact]
    public void AntiFlickerFilter_HoldsChangesBelowHalfAStep()
    {
        var filter = new AntiFlickerFilter();
        Span<Vector3> colors = [new Vector3(0.5f)];
        filter.Apply(colors);

        colors[0] = new Vector3(0.5f + (0.4f / 255));
        filter.Apply(colors);
        Assert.Equal(new Vector3(0.5f), colors[0]);

        colors[0] = new Vector3(0.5f + (0.6f / 255));
        filter.Apply(colors);
        Assert.Equal(new Vector3(0.5f + (0.6f / 255)), colors[0]);

        filter.Reset();
        colors[0] = new Vector3(0.1f);
        filter.Apply(colors);
        Assert.Equal(new Vector3(0.1f), colors[0]);
    }
}
