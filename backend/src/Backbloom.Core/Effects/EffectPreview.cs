using Backbloom.Core.Audio;
using Backbloom.Core.Color;
using Backbloom.Core.Layout;

namespace Backbloom.Core.Effects;

/// <summary>
/// A few seconds of an effect on the user's LEDs, rendered ahead of time for the UI's effect cards: each LED samples its
/// area of the effect's picture (or takes its own color from effects that set LED colors), as when the effect runs.
/// </summary>
/// <param name="Frames">RGB bytes, <see cref="LedCount"/> LEDs per frame, frame after frame; the frames loop.</param>
public sealed record EffectPreview(int FramesPerSecond, int LedCount, int FrameCount, byte[] Frames)
{
    public const int DefaultFramesPerSecond = 15;

    /// <summary>
    /// A 16:9 frame of LEDs for when there is no layout yet: <paramref name="top"/> along the top (left to right),
    /// <paramref name="side"/> down the right side, <paramref name="top"/> along the bottom (right to left) and
    /// <paramref name="side"/> up the left side, each sampling 10 % deep.
    /// </summary>
    public static LedLayout StandardFrame(int top = 16, int side = 9)
    {
        const double Depth = 0.1;
        var areas = new List<LedArea>();
        for (var i = 0; i < top; i++)
        {
            areas.Add(new LedArea((double)i / top, (double)(i + 1) / top, 0, Depth));
        }

        for (var i = 0; i < side; i++)
        {
            areas.Add(new LedArea(1 - Depth, 1, (double)i / side, (double)(i + 1) / side));
        }

        for (var i = top - 1; i >= 0; i--)
        {
            areas.Add(new LedArea((double)i / top, (double)(i + 1) / top, 1 - Depth, 1));
        }

        for (var i = side - 1; i >= 0; i--)
        {
            areas.Add(new LedArea(0, Depth, (double)i / side, (double)(i + 1) / side));
        }

        return new LedLayout(areas);
    }

    /// <summary>
    /// Runs <paramref name="definition"/> on simulated time: steps at the times it asks for, a music effect hearing
    /// <see cref="SyntheticMusic"/> (after one loop to settle, so its levels are learned), the same every time. 6 s of
    /// an effect, one 4 s loop of a music effect.
    /// </summary>
    public static EffectPreview Render(EffectDefinition definition, LedLayout layout, int framesPerSecond = DefaultFramesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(layout);
        var music = definition.IsMusic ? new MusicAnalysis() : null;
        music?.Start();
        var source = new SyntheticMusic();
        var block = new short[MusicAnalysis.BlockSize];
        var canvas = new EffectCanvas();
        var effect = definition.Create(new EffectContext(new MsvcRandom(42), music));
        effect.Init(canvas, Effect.LatchTimeMs);

        var ledCount = layout.Count;
        var leds = new Rgb8[ledCount];
        var shown = new double[ledCount * 3];
        var smoothed = new double[ledCount * 3];
        var settleMs = music is null ? 0 : SyntheticMusic.LoopSeconds * 1000;
        var lengthMs = music is null ? 6000 : SyntheticMusic.LoopSeconds * 1000;
        var frameMs = 1000.0 / framesPerSecond;
        var frameCount = (int)Math.Round(lengthMs / frameMs);
        var frames = new byte[frameCount * ledCount * 3];

        // effects with their own smoothing settle over that time; this follows them roughly
        var follow = definition.SmoothingTimeMs > 0 && !definition.SmoothingDirect
            ? 1 - Math.Exp(-frameMs / (definition.SmoothingTimeMs / 2.5))
            : 1.0;

        var blockMs = MusicAnalysis.BlockSize * 1000.0 / MusicAnalysis.SampleRate;
        double nextBlock = 0, nextStep = 0, nextFrame = 0;
        var frame = -(int)Math.Round(settleMs / frameMs);
        while (frame < frameCount)
        {
            if (music is not null && nextBlock <= nextStep && nextBlock <= nextFrame)
            {
                source.Fill(block);
                music.Analyze(block, out _);
                nextBlock += blockMs;
            }
            else if (nextStep <= nextFrame)
            {
                switch (effect.Render(canvas, leds))
                {
                    case EffectOutput.Leds:
                        for (var i = 0; i < ledCount; i++)
                        {
                            var on = !layout.Areas[i].Disabled;
                            shown[i * 3] = on ? leds[i].R : 0;
                            shown[(i * 3) + 1] = on ? leds[i].G : 0;
                            shown[(i * 3) + 2] = on ? leds[i].B : 0;
                        }

                        break;
                    case EffectOutput.Image:
                        Sample(canvas, layout, shown);
                        break;
                }

                nextStep += Math.Clamp(effect.SleepTimeMs, 1, Effect.MaxSleepTimeMs);
            }
            else
            {
                for (var i = 0; i < smoothed.Length; i++)
                {
                    smoothed[i] += (shown[i] - smoothed[i]) * follow;
                }

                if (frame >= 0)
                {
                    var offset = frame * ledCount * 3;
                    for (var i = 0; i < smoothed.Length; i++)
                    {
                        frames[offset + i] = (byte)Math.Clamp(Math.Round(smoothed[i]), 0, 255);
                    }
                }

                frame++;
                nextFrame += frameMs;
            }
        }

        return new EffectPreview(framesPerSecond, ledCount, frameCount, frames);
    }

    // The mean color of each LED's area of the picture (black for a disabled LED).
    private static void Sample(EffectCanvas canvas, LedLayout layout, double[] colors)
    {
        int w = canvas.Width, h = canvas.Height;
        for (var led = 0; led < layout.Count; led++)
        {
            var a = layout.Areas[led];
            double r = 0, g = 0, b = 0;
            var count = 0;
            if (!a.Disabled)
            {
                var x1 = Math.Min((int)(a.HMin * w), w - 1);
                var y1 = Math.Min((int)(a.VMin * h), h - 1);
                var x2 = Math.Max((int)Math.Ceiling(a.HMax * w), x1 + 1);
                var y2 = Math.Max((int)Math.Ceiling(a.VMax * h), y1 + 1);
                for (var y = y1; y < Math.Min(y2, h); y++)
                {
                    for (var x = x1; x < Math.Min(x2, w); x++)
                    {
                        var c = canvas[x, y];
                        r += c.R;
                        g += c.G;
                        b += c.B;
                        count++;
                    }
                }
            }

            count = Math.Max(count, 1);
            colors[led * 3] = r / count;
            colors[(led * 3) + 1] = g / count;
            colors[(led * 3) + 2] = b / count;
        }
    }
}
