using System.Numerics;
using Backbloom.Devices;

namespace Backbloom.Engine;

/// <summary>
/// Keeps a controller's LEDs within its own power limiter (WLED's automatic brightness limiter) before it has to step
/// in, and lowers that ceiling with the brightness. The controller's limiter cuts whatever is above its limit, so with a
/// power supply smaller than full white needs, the top of Home's brightness slider changed nothing on bright scenes.
/// Here each group the limiter holds to a budget (<see cref="ControllerTelemetry.PowerGroups"/>, as the driver reads it
/// from the controller) is held at <see cref="Margin"/> of the budget times the brightness instead: at full brightness
/// the lights give what the supply allows (as before), and below it every scene dims along the slider. The controller's
/// limiter stays in place as the safety net; LEDs it doesn't limit, or that the driver can't model, aren't touched.
/// </summary>
internal static class PowerBudget
{
    /// <summary>
    /// The share of the budget used: rounding and the estimates' differences stay below WLED's limiter, and its reading
    /// stays below the diagnostics' "at the power limit" warning (95 % of the limit).
    /// </summary>
    internal const float Margin = 0.93f;

    /// <summary>
    /// Holds each power group of <paramref name="colors"/> (one controller's nonlinear 0..1 values, as sent; in place)
    /// within <see cref="Margin"/> of its budget times <paramref name="share"/>, but never below the current the minimum
    /// glow needs. Only lowers values; does not allocate.
    /// </summary>
    /// <param name="share">The share of the full output the brightness leaves (<see cref="Core.Color.ColorProcessor.OutputShare"/>), 0..1.</param>
    /// <param name="minimalBacklight">The minimum glow the color settings keep (0 when off).</param>
    public static void Limit(Span<Vector3> colors, ControllerTelemetry? telemetry, float share, float minimalBacklight)
    {
        if (telemetry?.PowerGroups is not { Count: > 0 } groups || colors.IsEmpty)
        {
            return;
        }

        var own = telemetry.ForceMaxBrightness == true || telemetry.Brightness is not { } bri ? 1.0f : Math.Clamp(bri, 0, 255) / 255.0f;
        // index loops: an interface's enumerator would allocate per frame
        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var gain = Gain(colors, group, own, share, minimalBacklight);
            if (gain < 1.0f)
            {
                for (var r = 0; r < group.Ranges.Count; r++)
                {
                    var leds = Clip(colors, group.Ranges[r]);
                    for (var i = 0; i < leds.Length; i++)
                    {
                        leds[i] *= gain;
                    }
                }
            }
        }
    }

    /// <summary>The factor that keeps <paramref name="group"/> within its ceiling (1 when it already is).</summary>
    internal static float Gain(Span<Vector3> colors, PowerGroup group, float own, float share, float minimalBacklight)
    {
        if (group.BudgetMilliamps <= 0 || own <= 0.0f)
        {
            return 1.0f;
        }

        float demand = 0.0f, glow = 0.0f;
        for (var r = 0; r < group.Ranges.Count; r++)
        {
            var range = group.Ranges[r];
            var leds = Clip(colors, range);
            var full = 0.0f;
            foreach (var c in leds)
            {
                full += range.BrightestChannel ? MathF.Max(c.X, MathF.Max(c.Y, c.Z)) : (c.X + c.Y + c.Z) / 3.0f;
            }

            demand += full * (float)range.MilliampsPerLed;
            glow += leds.Length * (float)range.MilliampsPerLed;
        }

        demand *= own;
        var budget = Margin * group.BudgetMilliamps;
        var allowed = Math.Min(budget, MathF.Max(budget * Math.Clamp(share, 0.0f, 1.0f), glow * own * Math.Max(0.0f, minimalBacklight)));
        return demand > allowed && demand > 0.0f ? allowed / demand : 1.0f;
    }

    // The part of a range among the LEDs Backbloom sends (WLED may have more).
    private static Span<Vector3> Clip(Span<Vector3> colors, PowerRange range)
    {
        var start = Math.Clamp(range.Start, 0, colors.Length);
        return colors.Slice(start, Math.Clamp(range.Length, 0, colors.Length - start));
    }
}
