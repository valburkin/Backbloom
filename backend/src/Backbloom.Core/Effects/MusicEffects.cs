using Backbloom.Core.Audio;
using Backbloom.Core.Color;

namespace Backbloom.Core.Effects;

/// <summary>
/// A sound-reactive effect: draws its own 80x45 picture from the newest <see cref="MusicAnalysis"/> result, polled
/// every 17 ms. Port of <c>AnimationBaseMusic</c> (sources/effects/AnimationBaseMusic.cpp).
/// </summary>
public abstract class MusicEffect(MusicAnalysis? music) : Effect
{
    /// <summary>Music effect names start with this (the UI groups them by it).</summary>
    public const string NamePrefix = "Music: ";

    private uint _lastIndex;
    private int _isMulti;

    /// <summary>This effect's view of the moving color (C++ <c>_myTarget</c>).</summary>
    internal MovingTarget Target;

    public override void Init(EffectCanvas canvas, int latchTimeMs) => SleepTimeMs = 17;

    public sealed override EffectOutput Render(EffectCanvas canvas, Span<Rgb8> leds)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (music is null)
        {
            return EffectOutput.None;
        }

        if (!music.HasResult(this, ref _lastIndex, out var newData, ref _isMulti, out var stats) || !newData)
        {
            return DrawBetweenResults(canvas) ? EffectOutput.Image : EffectOutput.None;
        }

        canvas.Resize(EffectCanvas.DefaultWidth, EffectCanvas.DefaultHeight);
        if (!stats.Valid)
        {
            return EffectOutput.None;
        }

        Draw(canvas, stats, _isMulti);
        return EffectOutput.Image;
    }

    /// <param name="isMulti">Interpolation steps left until the next result (for <see cref="MusicStats.Value3Step"/>).</param>
    protected abstract void Draw(EffectCanvas canvas, MusicStats stats, int isMulti);

    /// <summary>A step without a new result: an effect that moves on its own (see <see cref="MusicAnimation"/>) draws.</summary>
    /// <returns>True when it drew a new picture.</returns>
    private protected virtual bool DrawBetweenResults(EffectCanvas canvas) => false;

    /// <summary>The bars of the stereo and quatro effects: from the middle of each side, as long as the loudness.</summary>
    protected static void DrawSideBars(EffectCanvas canvas, Rgb8 color, int value)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var hm = canvas.Height / 2;
        var h = Math.Min(hm * value / 255, 255);
        var h1 = Math.Max(hm - h, 0);
        var h2 = Math.Min(hm + h, canvas.Height - 1);
        if (h2 > h1)
        {
            var width = (int)(canvas.Width * 0.04);
            canvas.GradientVBox(0, h1, width, h2, color);
            canvas.GradientVBox(canvas.Width - 1 - width, h1, canvas.Width - 1, h2, color);
        }
    }
}

/// <summary>The whole picture in the sound's color (<c>Animation4Music_PulseMulti</c>).</summary>
public sealed class MusicPulseEffect(MusicAnalysis? music) : MusicEffect(music)
{
    public const string Name = "Music: fullscreen pulse (MULTI COLOR)";

    protected override void Draw(EffectCanvas canvas, MusicStats stats, int isMulti) =>
        canvas.FastBox(0, 0, canvas.Width - 1, canvas.Height - 1, stats.AverageColor);
}

/// <summary>Bars on the left and right side, as long as the loudness (<c>Animation4Music_StereoMulti</c>).</summary>
public sealed class MusicStereoEffect(MusicAnalysis? music) : MusicEffect(music)
{
    public const string Name = "Music: stereo for LED strip (MULTI COLOR)";

    protected override void Draw(EffectCanvas canvas, MusicStats stats, int isMulti)
    {
        canvas.Clear();
        DrawSideBars(canvas, MusicStats.RestoreFullLum(stats.AverageColor), stats.Value3Step(isMulti));
    }
}

/// <summary>Bars on all four sides, as long as the loudness (<c>Animation4Music_QuatroMulti</c>).</summary>
public sealed class MusicQuatroEffect(MusicAnalysis? music) : MusicEffect(music)
{
    public const string Name = "Music: quatro for LED strip (MULTI COLOR)";

    protected override void Draw(EffectCanvas canvas, MusicStats stats, int isMulti)
    {
        canvas.Clear();
        var color = MusicStats.RestoreFullLum(stats.AverageColor);
        var value = stats.Value3Step(isMulti);
        DrawSideBars(canvas, color, value);

        var wm = canvas.Width / 2;
        var w = Math.Min(wm * value / 255, 255);
        var w1 = Math.Max(wm - w, 0);
        var w2 = Math.Min(wm + w, canvas.Width - 1);
        if (w2 > w1)
        {
            var height = (int)(canvas.Height * 0.067);
            canvas.GradientHBox(w1, 0, w2, height, color);
            canvas.GradientHBox(w1, canvas.Height - 1 - height, w2, canvas.Height - 1, color);
        }
    }
}

/// <summary>
/// The last five colors as waves running from the middle of each side to the corners
/// (<c>Animation4Music_WavesPulse</c>).
/// </summary>
public sealed class MusicWavesEffect(MusicAnalysis? music) : MusicEffect(music)
{
    public const string Name = "Music: pulse waves for LED strip (MULTI COLOR)";

    private const int History = 5;

    private readonly Rgb8[] _history = new Rgb8[History];

    protected override void Draw(EffectCanvas canvas, MusicStats stats, int isMulti)
    {
        canvas.Clear();
        Array.Copy(_history, 1, _history, 0, History - 1);
        _history[^1] = stats.AverageColor;

        const int ScaleY = 15;
        for (var y = 0; y <= ScaleY / 2; y++)
        {
            var hm = canvas.Height / ScaleY;
            var h1 = canvas.Height * y / ScaleY;
            var h2 = canvas.Height * (ScaleY - y) / ScaleY;
            var width = (int)(canvas.Width * 0.04);
            var color = Blend(y, ScaleY / 2);
            canvas.GradientVBox(0, h1, width, h1 + hm, color);
            if (y != ScaleY / 2)
            {
                canvas.GradientVBox(0, h2, width, h2 - hm, color);
            }

            canvas.GradientVBox(canvas.Width - 1 - width, h1, canvas.Width - 1, h1 + hm, color);
            if (y != ScaleY / 2)
            {
                canvas.GradientVBox(canvas.Width - 1 - width, h2, canvas.Width - 1, h2 - hm, color);
            }
        }

        const int ScaleX = 27;
        for (var x = 0; x <= ScaleX / 2; x++)
        {
            var wm = canvas.Width / ScaleX;
            var w1 = canvas.Width * x / ScaleX;
            var w2 = canvas.Width * (ScaleX - x) / ScaleX;
            var height = (int)(canvas.Height * 0.067);
            var color = Blend(x, ScaleX / 2);
            canvas.GradientHBox(w1, 0, w1 + wm + 1, height, color);
            if (x != ScaleX / 2)
            {
                canvas.GradientHBox(w2, 0, w2 - wm, height, color);
            }

            canvas.GradientHBox(w1, canvas.Height - 1 - height, w1 + wm + 1, canvas.Height - 1, color);
            if (x != ScaleX / 2)
            {
                canvas.GradientHBox(w2, canvas.Height - 1 - height, w2 - wm, canvas.Height - 1, color);
            }
        }
    }

    // The history color at step / steps of the way from the oldest to the newest, mixed in percent.
    private Rgb8 Blend(int step, int steps)
    {
        int index = step * (History - 1) / steps, w1, w2;
        if (index > History - 2)
        {
            index = History - 2;
            w1 = 0;
            w2 = 100;
        }
        else
        {
            w2 = Math.Min(step * (History - 1) % steps * 100 / steps, 100);
            w1 = 100 - w2;
        }

        var a = _history[index];
        var b = _history[index + 1];
        return new Rgb8(
            (byte)Math.Min(((a.R * w1) + (b.R * w2)) / 100, 255),
            (byte)Math.Min(((a.G * w1) + (b.G * w2)) / 100, 255),
            (byte)Math.Min(((a.B * w1) + (b.B * w2)) / 100, 255));
    }
}
