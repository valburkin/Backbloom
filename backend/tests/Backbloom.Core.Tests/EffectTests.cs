using Backbloom.Core.Audio;
using Backbloom.Core.Color;
using Backbloom.Core.Effects;

namespace Backbloom.Core.Tests;

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
    public void MusicAnalysis_CountsBeatsAndReportsBandLevels()
    {
        var music = new MusicAnalysis();
        music.Start();
        var source = new SyntheticMusic();
        var block = new short[MusicAnalysis.BlockSize];
        var blocksPerLoop = (int)(SyntheticMusic.LoopSeconds * MusicAnalysis.SampleRate / MusicAnalysis.BlockSize);
        var effect = new MusicBassGlowEffect(music);
        uint lastIndex = 0;
        var isMulti = 0;
        MusicStats stats = default;
        for (var b = 0; b < 2 * blocksPerLoop; b++)
        {
            source.Fill(block);
            music.Analyze(block, out _);
            music.HasResult(effect, ref lastIndex, out _, ref isMulti, out stats);
        }

        // 120 BPM for 8 s: 16 kicks (the first may come before the levels are learned)
        Assert.InRange(stats.Beats, 14u, 17u);
        Assert.True(stats.Valid);
        Assert.Equal((byte)(stats.BandLevels >> 8), stats.Band(1));
        Assert.Contains(Enumerable.Range(0, MusicStats.BandCount), i => stats.Band(i) > 0);

        // a steady tone: beats only while it starts, none once the average caught up
        var steady = new MusicAnalysis();
        steady.Start();
        for (var i = 0; i < block.Length; i++)
        {
            block[i] = (short)(12000 * Math.Sin(2 * Math.PI * 4 * i / block.Length));
        }

        lastIndex = 0;
        uint onset = 0;
        for (var b = 0; b < 100; b++)
        {
            steady.Analyze(block, out _);
            steady.HasResult(effect, ref lastIndex, out _, ref isMulti, out stats);
            onset = b == 20 ? stats.Beats : onset;
        }

        Assert.InRange(onset, 0u, 2u);
        Assert.Equal(onset, stats.Beats);
    }

    [Theory]
    [MemberData(nameof(EffectNames))]
    public void Preview_ShowsTheEffectMovingTheSameEveryTime(string name)
    {
        var definition = EffectCatalog.Find(name)!;
        var layout = EffectPreview.StandardFrame();
        var preview = EffectPreview.Render(definition, layout);

        Assert.Equal(50, preview.LedCount);
        Assert.Equal(preview.FrameCount * preview.LedCount * 3, preview.Frames.Length);
        Assert.Equal(preview.Frames, EffectPreview.Render(definition, layout).Frames);
        Assert.Contains(preview.Frames, b => b > 16);
        var frameSize = preview.LedCount * 3;
        var first = preview.Frames.AsSpan(0, frameSize).ToArray();
        Assert.Contains(Enumerable.Range(1, preview.FrameCount - 1), f => !preview.Frames.AsSpan(f * frameSize, frameSize).SequenceEqual(first));
    }

    [Fact]
    public void Preview_SamplesEachLedsOwnAreaAndKeepsDisabledLedsDark()
    {
        // two strips on the same top edge (like one around a monitor and one on the ceiling), one disabled LED
        Backbloom.Core.Layout.LedArea Top(double from, double to, bool disabled = false) => new(from, to, 0, 0.1, Disabled: disabled);
        var layout = new Backbloom.Core.Layout.LedLayout([Top(0, 0.1), Top(0.9, 1), Top(0.9, 1), Top(0, 0.1), Top(0.4, 0.6, disabled: true)]);
        var preview = EffectPreview.Render(EffectCatalog.Find("Rainbow swirl")!, layout);

        Assert.Equal(5, preview.LedCount);
        var frame = preview.Frames.AsSpan(0, 15).ToArray();
        Assert.Equal(frame[0..3], frame[9..12]);
        Assert.Equal(frame[3..6], frame[6..9]);

        // LEDs with the same area are sampled once: the same frames as each area on its own
        var single = EffectPreview.Render(EffectCatalog.Find("Rainbow swirl")!, new Backbloom.Core.Layout.LedLayout([Top(0, 0.1), Top(0.9, 1)]));
        Assert.Equal(single.Frames.AsSpan(0, 6).ToArray(), frame[0..6]);
        Assert.NotEqual(frame[0..3], frame[3..6]);
        Assert.Equal(new byte[3], frame[12..15]);
    }

    [Fact]
    public void BeatRipple_SendsARingOnEveryBeat()
    {
        var music = new MusicAnalysis();
        music.Start();
        var source = new SyntheticMusic();
        var block = new short[MusicAnalysis.BlockSize];
        var canvas = new EffectCanvas();
        var effect = new MusicBeatRippleEffect(music);
        effect.Init(canvas, Effect.LatchTimeMs);

        // the middle of the picture lights up shortly after a beat, the corner later
        var middleBright = 0;
        for (var step = 0; step < 470; step++)
        {
            if (step % 3 == 0)
            {
                source.Fill(block);
                music.Analyze(block, out _);
            }

            effect.Render(canvas, []);
            var c = canvas[40, 22];
            middleBright += c.R + c.G + c.B > 200 ? 1 : 0;
        }

        Assert.InRange(middleBright, 10, 200);
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
