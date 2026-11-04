using System.Numerics;
using Backbloom.Devices;

namespace Backbloom.Engine.Tests;

public sealed class PowerBudgetTests
{
    // the owner's strip: 168 LEDs at 60 mA under a 5313 mA limit (budget 5313 - 100 - 168 = 5045 mA, full white 10080 mA)
    private static readonly ControllerTelemetry s_wled = new(DateTimeOffset.UnixEpoch)
    {
        PowerGroups = [new PowerGroup(5045, [new PowerRange(0, 168, 60, false)])],
        Brightness = 255,
    };

    private static Vector3[] Frame(float value, int count = 168) => Enumerable.Repeat(new Vector3(value), count).ToArray();

    // WLED's estimate of LEDs [start, start + count) at 60 mA (without its own 100 mA and 1 mA per LED)
    private static float Demand(Vector3[] colors, int start = 0, int count = 168) =>
        colors.Skip(start).Take(count).Sum(c => (c.X + c.Y + c.Z) / 3.0f) * 60;

    private static Vector3[] Limited(Vector3[] colors, ControllerTelemetry? telemetry, float share, float glow = 0)
    {
        PowerBudget.Limit(colors, telemetry, share, glow);
        return colors;
    }

    [Fact]
    public void AControllerWithoutPowerGroups_IsNotLimited()
    {
        Assert.Equal(Frame(1), Limited(Frame(1), null, 1));
        Assert.Equal(Frame(1), Limited(Frame(1), s_wled with { PowerGroups = null }, 1));
        Assert.Equal(Frame(1), Limited(Frame(1), s_wled with { PowerGroups = [] }, 0.1f));
    }

    [Fact]
    public void AFrameWithinTheBudget_IsLeftAlone()
    {
        Assert.Equal(Frame(0.4f), Limited(Frame(0.4f), s_wled, 1));
        Assert.Equal(Frame(0), Limited(Frame(0), s_wled, 0));
    }

    [Fact]
    public void FullWhite_IsHeldUnderWledsOwnLimit()
    {
        var white = Limited(Frame(1), s_wled, 1);
        Assert.Equal(PowerBudget.Margin * 5045, Demand(white), 1.0f);
    }

    [Fact]
    public void TheBrightness_LowersTheCeiling_SoBrightScenesDimAlongTheSlider()
    {
        // the brightness has already scaled the frame (half the output); the limit comes down with it
        Assert.Equal(PowerBudget.Margin * 5045 * 0.5f, Demand(Limited(Frame(0.5f), s_wled, 0.5f)), 1.0f);

        // full white at 80% and at 100% of the output: no longer the same light
        var at80 = Demand(Limited(Frame(0.8f), s_wled, 0.8f));
        var at100 = Demand(Limited(Frame(1), s_wled, 1));
        Assert.Equal(0.8f, at80 / at100, 0.001f);

        // a dark scene keeps its light at full brightness
        Assert.Equal(Frame(0.3f), Limited(Frame(0.3f), s_wled, 1));
    }

    [Fact]
    public void LedsTheLimiterDoesNotHold_AreLeftAlone()
    {
        // 50 limited LEDs (budget 300 mA) next to 118 on an output WLED doesn't limit (WLED 0.15)
        var telemetry = s_wled with { PowerGroups = [new PowerGroup(300, [new PowerRange(0, 50, 60, false)])] };
        var frame = Limited(Frame(1), telemetry, 1);

        Assert.Equal(PowerBudget.Margin * 300, Demand(frame, 0, 50), 0.5f);
        Assert.All(frame[50..], c => Assert.Equal(Vector3.One, c));
    }

    [Fact]
    public void EachGroup_HasItsOwnBudget()
    {
        var telemetry = s_wled with
        {
            PowerGroups = [new PowerGroup(1000, [new PowerRange(0, 84, 60, false)]), new PowerGroup(6000, [new PowerRange(84, 84, 60, false)])],
        };
        var frame = Limited(Frame(1), telemetry, 1);

        Assert.Equal(PowerBudget.Margin * 1000, Demand(frame, 0, 84), 0.5f);
        Assert.Equal(84 * 60, Demand(frame, 84, 84), 0.5f);
    }

    [Fact]
    public void TheMinimumGlow_Stays_AtTheBottomOfTheSlider()
    {
        // 150 LEDs at 55 mA under WLED's default 850 mA: a 0.5 % glow (about 41 mA) is more than 2 % of the budget
        var small = s_wled with { PowerGroups = [new PowerGroup(600, [new PowerRange(0, 150, 55, false)])] };
        Assert.All(Limited(Frame(0.005f, 150), small, 0, 0.005f), c => Assert.Equal(0.005f, c.X, 1e-6f));

        // without a glow, the bottom of the slider is dark
        Assert.Equal(0, Demand(Limited(Frame(1), s_wled, 0)), 0.001f);
    }

    [Fact]
    public void TheControllersOwnBrightness_AndTheWs2815Model_CountAsWledCountsThem()
    {
        // at half its own brightness WLED estimates half the current; with "force max brightness" it ignores it
        Assert.Equal(Frame(0.9f), Limited(Frame(0.9f), s_wled with { Brightness = 128 }, 1));
        Assert.NotEqual(Frame(0.9f), Limited(Frame(0.9f), s_wled with { Brightness = 128, ForceMaxBrightness = true }, 1));

        // pure red: a third of white by the mean of the channels, all of it by the brightest channel
        var red = Enumerable.Repeat(new Vector3(1, 0, 0), 168).ToArray();
        Assert.Equal(red, Limited([.. red], s_wled, 1));
        var ws2815 = s_wled with { PowerGroups = [new PowerGroup(5045, [new PowerRange(0, 168, 60, true)])] };
        Assert.Equal(PowerBudget.Margin * 5045 / (168 * 60f), Limited([.. red], ws2815, 1)[0].X, 0.001f);
    }

    [Fact]
    public void AGroupBeyondTheLedsSent_OrWithoutABudget_ChangesNothing()
    {
        Assert.Equal(Frame(1, 10), Limited(Frame(1, 10), s_wled with { PowerGroups = [new PowerGroup(10, [new PowerRange(20, 30, 60, false)])] }, 1));
        Assert.Equal(Frame(1), Limited(Frame(1), s_wled with { PowerGroups = [new PowerGroup(0, [new PowerRange(0, 168, 60, false)])] }, 1));
    }

    [Fact]
    public void Limit_OnlyEverLowersValues_AndStaysWithinTheBudget()
    {
        var random = new Random(7);
        var telemetry = s_wled with
        {
            PowerGroups = [new PowerGroup(900, [new PowerRange(0, 60, 60, false), new PowerRange(100, 30, 12, true)]), new PowerGroup(2000, [new PowerRange(60, 40, 55, false)])],
        };
        float[] shares = [0, 0.01f, 0.3f, 1, 1.5f, float.NaN];
        for (var round = 0; round < 200; round++)
        {
            var frame = Enumerable.Range(0, 168).Select(_ => new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle())).ToArray();
            var share = shares[round % shares.Length];
            var limited = Limited([.. frame], telemetry, share, round % 3 == 0 ? 0.05f : 0);

            for (var i = 0; i < frame.Length; i++)
            {
                Assert.True(limited[i].X <= frame[i].X && limited[i].Y <= frame[i].Y && limited[i].Z <= frame[i].Z);
            }

            Assert.True(Demand(limited, 60, 40) * 55 / 60 <= 2000 * PowerBudget.Margin + 0.5f);
        }
    }

    [Fact]
    public void Limit_DoesNotAllocate()
    {
        // lists as a driver builds them, several groups and ranges
        var telemetry = s_wled with
        {
            PowerGroups = new List<PowerGroup>
            {
                new(900, new List<PowerRange> { new(0, 60, 60, false), new(100, 30, 12, true) }),
                new(2000, new List<PowerRange> { new(60, 40, 55, false) }),
            },
        };
        var white = Frame(1);
        var frame = Frame(1);
        PowerBudget.Limit(frame, telemetry, 0.7f, 0.01f);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            white.CopyTo(frame, 0);
            PowerBudget.Limit(frame, telemetry, 0.7f, 0.01f);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(frame[0].X < 1);
    }
}
