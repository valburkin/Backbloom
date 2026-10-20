using Overglow.Core.Color;

namespace Overglow.Core.Effects;

/// <summary>
/// A flickering candle: every LED gets a random brightness and a slightly shifted hue of orange each step. Port of
/// <c>Animation_CandleLight</c> (sources/effects/Animation_CandleLight.cpp; algorithm by penfold42 after
/// https://cpldcpu.com/2013/12/08/hacking-a-candleflicker-led/), preset "Candle".
/// </summary>
public sealed class CandleEffect(IEffectRandom random) : Effect
{
    private const int ColorShift = 4;

    private ushort _hue;
    private byte _saturation;

    public override void Init(EffectCanvas canvas, int latchTimeMs)
    {
        Hsv8.FromRgb(new Rgb8(255, 138, 0), out _hue, out _saturation, out _);
        SleepTimeMs = 200;
    }

    public override EffectOutput Render(EffectCanvas canvas, Span<Rgb8> leds)
    {
        if (leds.Length > 1)
        {
            for (var i = 0; i < leds.Length; i++)
            {
                leds[i] = CandleColor();
            }
        }

        return EffectOutput.Leds;
    }

    private Rgb8 CandleColor()
    {
        var shift = (random.Rand() % ((ColorShift * 2) + 1)) - ColorShift;
        var hue = (_hue + shift + 360) % 360;
        var level = random.Rand() % 16;
        var tries = 0;
        while ((level & 0x0c) == 0 && tries++ < 100)
        {
            level = random.Rand() % 16;
        }

        return Hsv8.ToRgb((ushort)hue, _saturation, (byte)Math.Min(level * 17, 255));
    }
}
