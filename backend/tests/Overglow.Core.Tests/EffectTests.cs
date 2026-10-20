using Overglow.Core.Audio;
using Overglow.Core.Color;
using Overglow.Core.Effects;

namespace Overglow.Core.Tests;

[Collection(AllocationMeasurements.Name)]
public sealed class EffectTests
{
    public static TheoryData<string> EffectNames() => [.. EffectCatalog.All.Select(e => e.Name)];

    [Fact]
    public void MsvcRandom_IsTheMicrosoftCrtSequence()
    {
        // rand() after srand(1), the CRT's start state
        var random = new MsvcRandom(1);
        Assert.Equal([41, 18467, 6334, 26500, 19169], Enumerable.Range(0, 5).Select(_ => random.Rand()));
    }

    [Theory]
    [MemberData(nameof(EffectNames))]
    public void Effect_StepsWithoutAllocating(string name)
    {
        var music = new MusicAnalysis();
        music.Start();
        var samples = new short[MusicAnalysis.BlockSize];
        var definition = EffectCatalog.Find(name)!;
        var canvas = new EffectCanvas();
        var effect = definition.Create(new EffectContext(new MsvcRandom(7), music));
        effect.Init(canvas, Effect.LatchTimeMs);
        var leds = new Rgb8[168];
        var block = 0;

        void Step()
        {
            // a new sound block every third step, as at 17 ms steps and 46 ms blocks
            if (definition.IsMusic && block++ % 3 == 0)
            {
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (short)(((i * (7 + (block % 5))) % 200 - 100) * (50 + (block % 40) * 20));
                }

                music.Analyze(samples, out _);
            }

            effect.Render(canvas, leds);
        }

        void Steps()
        {
            for (var i = 0; i < 400; i++)
            {
                Step();
            }
        }

        Assert.Equal(0, Allocations.Measure(Steps, Steps));
        Assert.InRange(effect.SleepTimeMs, 1, 1000);
    }

    [Fact]
    public void MusicEffect_DrawsOnlyWithNewSound()
    {
        var music = new MusicAnalysis { Smoothing = false };
        var canvas = new EffectCanvas();
        var effect = EffectCatalog.Find(MusicPulseEffect.Name)!.Create(new EffectContext(new MsvcRandom(1), music));
        effect.Init(canvas, Effect.LatchTimeMs);
        var tone = new short[MusicAnalysis.BlockSize];
        for (var i = 0; i < tone.Length; i++)
        {
            tone[i] = (short)(12000 * Math.Sin(2 * Math.PI * 4 * i / tone.Length));
        }

        // no capture running: blocks are ignored
        Assert.False(music.Analyze(tone, out _));
        Assert.Equal(EffectOutput.None, effect.Render(canvas, []));

        music.Start();
        var drawn = 0;
        for (var b = 0; b < 30; b++)
        {
            Assert.True(music.Analyze(tone, out var silent));
            Assert.False(silent);
            drawn += effect.Render(canvas, []) == EffectOutput.Image ? 1 : 0;

            // without smoothing nothing more until the next block
            Assert.Equal(EffectOutput.None, effect.Render(canvas, []));
        }

        Assert.InRange(drawn, 20, 30);
        Assert.NotEqual(Rgb8.Black, canvas[40, 22]);
    }

    [Fact]
    public void Canvas_BoxesAreClampedToThePicture()
    {
        var canvas = new EffectCanvas(10, 6);
        canvas.FastBox(-3, 4, 20, 9, new Rgb8(1, 2, 3));

        // -3 wraps to a large uint16 coordinate and is clamped to the right edge, as in C++
        Assert.Equal(Rgb8.Black, canvas[0, 5]);
        Assert.Equal(new Rgb8(1, 2, 3), canvas[9, 5]);
        Assert.Equal(new Rgb8(1, 2, 3), canvas[9, 4]);
        Assert.Equal(Rgb8.Black, canvas[9, 3]);
    }
}
