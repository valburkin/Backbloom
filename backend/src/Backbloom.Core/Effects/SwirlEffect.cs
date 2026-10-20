using Backbloom.Core.Color;

namespace Backbloom.Core.Effects;

/// <summary>
/// A rotating rainbow (conical gradient), one degree per step. Port of <c>Animation_Swirl</c>
/// (sources/effects/Animation_Swirl.cpp): presets "Rainbow swirl" (20 s per turn) and "Rainbow swirl fast" (7 s).
/// </summary>
public sealed class SwirlEffect(double rotationTimeSeconds) : Effect
{
    private static readonly byte[] s_stops = BuildStops();

    private int _angle;

    public override void Init(EffectCanvas canvas, int latchTimeMs)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        canvas.Resize(EffectCanvas.DefaultWidth, EffectCanvas.DefaultHeight);
        SleepTimeMs = StepTime(latchTimeMs, (int)rotationTimeSeconds);
        _angle = 0;
    }

    public override EffectOutput Render(EffectCanvas canvas, Span<Rgb8> leds)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        _angle++;
        if (_angle > 360)
        {
            _angle = 0;
        }

        canvas.ConicalFill(_angle, s_stops);
        return EffectOutput.Image;
    }

    // Animation_Swirl::getSTime: one turn in 360 steps, at least max(latch time, 15 ms) per step.
    private static int StepTime(int latchTimeMs, int rotationTime)
    {
        var sleepTime = Math.Max(0.1, rotationTime) / 360.0;
        var minStepTime = Math.Max(latchTimeMs, 15) / 1000.0;
        if (minStepTime > sleepTime)
        {
            sleepTime = minStepTime;
        }

        return (int)Math.Round(sleepTime * 1000.0, MidpointRounding.AwayFromZero);
    }

    // Red, orange, yellow, green, cyan, blue, magenta and back to red; positions as (uint8_t)(255 / 12 * k).
    private static byte[] BuildStops()
    {
        var x = 255.0 / 12;
        return
        [
            0, 255, 0, 0,
            (byte)(x * 0.8), 255, 128, 0,
            (byte)(x * 2.2), 255, 255, 0,
            (byte)(x * 3.5), 128, 255, 0,
            (byte)(x * 4.3), 0, 255, 0,
            (byte)(x * 5.1), 0, 255, 128,
            (byte)(x * 6.0), 0, 255, 255,
            (byte)(x * 6.9), 0, 128, 255,
            (byte)(x * 7.9), 0, 0, 255,
            (byte)(x * 8.9), 128, 0, 255,
            (byte)(x * 10), 255, 0, 255,
            (byte)(x * 11), 255, 0, 128,
            255, 255, 0, 0,
        ];
    }
}
