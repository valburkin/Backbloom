using Backbloom.Core.Audio;
using Backbloom.Core.Color;

namespace Backbloom.Core.Effects;

/// <summary>
/// A music effect that moves between sound results: it takes each result in <see cref="Update"/> and paints the
/// picture on every 17 ms step in <see cref="Paint"/>, so ripples, scrolling and falling levels stay smooth although
/// results come only 21.5 times a second. The effects built on it are new in Backbloom (HyperHDR had none of them).
/// </summary>
public abstract class MusicAnimation(MusicAnalysis? music) : MusicEffect(music)
{
    private bool _started;

    protected sealed override void Draw(EffectCanvas canvas, MusicStats stats, int isMulti)
    {
        _started = true;
        Update(stats, isMulti);
        Paint(canvas);
    }

    private protected sealed override bool DrawBetweenResults(EffectCanvas canvas)
    {
        if (!_started)
        {
            return false;
        }

        canvas.Resize(EffectCanvas.DefaultWidth, EffectCanvas.DefaultHeight);
        Paint(canvas);
        return true;
    }

    /// <summary>A new result (also its interpolated steps, with <paramref name="isMulti"/> 1 and 0).</summary>
    protected abstract void Update(MusicStats stats, int isMulti);

    /// <summary>One step: moves the animation on by 17 ms and paints the whole picture.</summary>
    protected abstract void Paint(EffectCanvas canvas);

    /// <summary>The hue of <paramref name="color"/> at full saturation and brightness (black stays a dim white).</summary>
    protected static Rgb8 Vivid(Rgb8 color)
    {
        Hsv8.FromRgb(color, out var hue, out var saturation, out _);
        return Hsv8.ToRgb(hue, Math.Max(saturation, (byte)200), 255);
    }

    protected static Rgb8 Scale(Rgb8 color, float factor) => new(
        (byte)Math.Clamp(color.R * factor, 0, 255),
        (byte)Math.Clamp(color.G * factor, 0, 255),
        (byte)Math.Clamp(color.B * factor, 0, 255));
}

/// <summary>
/// Where each pixel of the picture lies on the frame's edge, as the way from the middle of the bottom edge (0) up either
/// side to the middle of the top edge (1); left and right are mirrored. A pixel takes the position of its nearest edge,
/// so LEDs that sample deeper into the picture see the same colors as the edge.
/// </summary>
internal sealed class EdgePositions
{
    private readonly float[] _positions;

    public EdgePositions(int width = EffectCanvas.DefaultWidth, int height = EffectCanvas.DefaultHeight)
    {
        Width = width;
        Height = height;
        _positions = new float[width * height];
        var halfWidth = (width - 1) / 2.0f;
        var length = (width - 1) + (height - 1.0f);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var side = Math.Min(x, width - 1 - x);
                var top = y;
                var bottom = height - 1 - y;
                var fromMiddle = Math.Abs(x - halfWidth);
                float s;
                if (bottom <= top && bottom <= side)
                {
                    s = fromMiddle;
                }
                else if (top <= side)
                {
                    s = halfWidth + (height - 1) + (halfWidth - fromMiddle);
                }
                else
                {
                    s = halfWidth + (height - 1 - y);
                }

                _positions[(y * width) + x] = s / length;
            }
        }
    }

    public int Width { get; }

    public int Height { get; }

    public float this[int x, int y] => _positions[(y * Width) + x];
}

/// <summary>The spectrum around the frame: bass at the bottom, treble at the top, each band in its own color.</summary>
public sealed class MusicSpectrumEffect(MusicAnalysis? music) : MusicAnimation(music)
{
    public const string Name = "Music: spectrum";

    private const int Bands = MusicStats.BandCount;

    private readonly EdgePositions _edges = new();
    private readonly float[] _targets = new float[Bands];
    private readonly float[] _levels = new float[Bands];
    private readonly Rgb8[] _colors = new Rgb8[Bands];

    protected override void Update(MusicStats stats, int isMulti)
    {
        for (var i = 0; i < Bands; i++)
        {
            _targets[i] = stats.Band(i) / 255.0f;
        }
    }

    protected override void Paint(EffectCanvas canvas)
    {
        for (var i = 0; i < Bands; i++)
        {
            // jump up, fall back slowly
            _levels[i] = _targets[i] >= _levels[i] ? _targets[i] : Math.Max(_targets[i], _levels[i] - Math.Max(0.012f, _levels[i] * 0.06f));
            _colors[i] = Hsv8.ToRgb((ushort)(i * 270 / (Bands - 1)), 255, (byte)(_levels[i] * 255));
        }

        for (var y = 0; y < _edges.Height; y++)
        {
            for (var x = 0; x < _edges.Width; x++)
            {
                // between the centers of the two nearest bands
                var position = Math.Clamp((_edges[x, y] * Bands) - 0.5f, 0, Bands - 1);
                var band = Math.Min((int)position, Bands - 2);
                var t = position - band;
                var a = _colors[band];
                var b = _colors[band + 1];
                canvas.SetPixel(x, y, new Rgb8(
                    (byte)((a.R * (1 - t)) + (b.R * t)),
                    (byte)((a.G * (1 - t)) + (b.G * t)),
                    (byte)((a.B * (1 - t)) + (b.B * t))));
            }
        }
    }
}

/// <summary>A level meter up both sides: green, then yellow, then red at the top, with a slowly falling peak.</summary>
public sealed class MusicVuMeterEffect : MusicAnimation
{
    public const string Name = "Music: VU meter";

    private const int PeakHoldSteps = 40;
    private const float PeakWidth = 0.025f;

    private readonly EdgePositions _edges = new();
    private readonly Rgb8[] _lit;
    private readonly Rgb8[] _unlit;
    private float _target;
    private float _level;
    private float _peak;
    private int _peakHold;

    public MusicVuMeterEffect(MusicAnalysis? music)
        : base(music)
    {
        _lit = new Rgb8[_edges.Width * _edges.Height];
        _unlit = new Rgb8[_lit.Length];
        for (var y = 0; y < _edges.Height; y++)
        {
            for (var x = 0; x < _edges.Width; x++)
            {
                // green up to the middle, then through yellow to red near the top
                var s = _edges[x, y];
                var hue = (ushort)(s < 0.5f ? 120 : Math.Max(0, 120 - ((s - 0.5f) / 0.4f * 120)));
                _lit[(y * _edges.Width) + x] = Hsv8.ToRgb(hue, 255, 255);
                _unlit[(y * _edges.Width) + x] = Hsv8.ToRgb(hue, 255, 18);
            }
        }
    }

    // The loudness is relative to the recent maximum, so it stays high in loud music; the curve leaves room to move.
    protected override void Update(MusicStats stats, int isMulti) => _target = MathF.Pow(stats.Value3Step(isMulti) / 255.0f, 2.2f);

    protected override void Paint(EffectCanvas canvas)
    {
        _level = _target >= _level ? _target : Math.Max(_target, _level - 0.03f);
        if (_level >= _peak)
        {
            _peak = _level;
            _peakHold = PeakHoldSteps;
        }
        else if (_peakHold > 0)
        {
            _peakHold--;
        }
        else
        {
            _peak = Math.Max(_level, _peak - 0.008f);
        }

        for (var y = 0; y < _edges.Height; y++)
        {
            for (var x = 0; x < _edges.Width; x++)
            {
                var s = _edges[x, y];
                var i = (y * _edges.Width) + x;
                canvas.SetPixel(x, y, _peak > 0.02f && Math.Abs(s - _peak) < PeakWidth ? new Rgb8(255, 255, 255) : s <= _level ? _lit[i] : _unlit[i]);
            }
        }
    }
}

/// <summary>Each beat sends a ring in the music's color from the middle of the picture out over the edges.</summary>
public sealed class MusicBeatRippleEffect : MusicAnimation
{
    public const string Name = "Music: beat ripple";

    private const int MaxRipples = 6;
    private const float EndRadius = 1.3f;
    private const float RadiusStep = EndRadius / 50; // about 0.85 s from the middle to the end
    private const float RingWidth = 0.16f;

    private readonly float[] _distances;
    private readonly float[] _radii = new float[MaxRipples];
    private readonly Rgb8[] _ringColors = new Rgb8[MaxRipples];
    private Rgb8 _glow;
    private uint _beats;
    private bool _seen;

    public MusicBeatRippleEffect(MusicAnalysis? music)
        : base(music)
    {
        const int Width = EffectCanvas.DefaultWidth;
        const int Height = EffectCanvas.DefaultHeight;

        // 0 in the middle, 1 in the corners, about 0.7 in the middle of each edge
        _distances = new float[Width * Height];
        var cx = (Width - 1) / 2.0f;
        var cy = (Height - 1) / 2.0f;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var dx = (x - cx) / cx;
                var dy = (y - cy) / cy;
                _distances[(y * Width) + x] = MathF.Sqrt((dx * dx) + (dy * dy)) / MathF.Sqrt(2);
            }
        }

        _radii.AsSpan().Fill(-1);
    }

    protected override void Update(MusicStats stats, int isMulti)
    {
        _glow = Scale(stats.AverageColor, 0.15f);
        if (_seen && stats.Beats != _beats)
        {
            // a free slot, or else the oldest ring (the largest one)
            var slot = 0;
            for (var i = 0; i < MaxRipples; i++)
            {
                if (_radii[i] < 0)
                {
                    slot = i;
                    break;
                }

                if (_radii[i] > _radii[slot])
                {
                    slot = i;
                }
            }

            _radii[slot] = 0;
            _ringColors[slot] = Vivid(stats.AverageColor);
        }

        _beats = stats.Beats;
        _seen = true;
    }

    protected override void Paint(EffectCanvas canvas)
    {
        for (var i = 0; i < MaxRipples; i++)
        {
            if (_radii[i] >= 0)
            {
                _radii[i] += RadiusStep;
                if (_radii[i] > EndRadius)
                {
                    _radii[i] = -1;
                }
            }
        }

        var width = canvas.Width;
        for (var y = 0; y < canvas.Height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var d = _distances[(y * width) + x];
                float r = _glow.R, g = _glow.G, b = _glow.B;
                for (var i = 0; i < MaxRipples; i++)
                {
                    if (_radii[i] < 0)
                    {
                        continue;
                    }

                    var k = (1 - (Math.Abs(d - _radii[i]) / RingWidth)) * (1 - (_radii[i] / EndRadius));
                    if (k > 0)
                    {
                        r += _ringColors[i].R * k;
                        g += _ringColors[i].G * k;
                        b += _ringColors[i].B * k;
                    }
                }

                canvas.SetPixel(x, y, new Rgb8((byte)Math.Min(r, 255), (byte)Math.Min(g, 255), (byte)Math.Min(b, 255)));
            }
        }
    }
}

/// <summary>The music's colors enter at the bottom and flow up both sides to the top.</summary>
public sealed class MusicWaterfallEffect(MusicAnalysis? music) : MusicAnimation(music)
{
    public const string Name = "Music: waterfall";

    // 96 steps of 17 ms: a color takes about 1.6 s from the bottom to the top
    private const int Length = 96;

    private readonly EdgePositions _edges = new();
    private readonly Rgb8[] _history = new Rgb8[Length];
    private Rgb8 _current;
    private int _head;

    // the spectrum color is a mix and often pale: its hue, saturated, as bright as the music is loud
    protected override void Update(MusicStats stats, int isMulti)
    {
        Hsv8.FromRgb(stats.AverageColor, out var hue, out var saturation, out var value);
        _current = Hsv8.ToRgb(hue, Math.Max(saturation, (byte)210), value);
    }

    protected override void Paint(EffectCanvas canvas)
    {
        _head = (_head + 1) % Length;
        _history[_head] = _current;
        for (var y = 0; y < _edges.Height; y++)
        {
            for (var x = 0; x < _edges.Width; x++)
            {
                var age = (int)(_edges[x, y] * (Length - 1));
                canvas.SetPixel(x, y, _history[(_head - age + Length) % Length]);
            }
        }
    }
}

/// <summary>A calm glow in a slowly changing color that swells with the bass.</summary>
public sealed class MusicBassGlowEffect(MusicAnalysis? music) : MusicAnimation(music)
{
    public const string Name = "Music: bass glow";

    // 0.05 degrees per step: the color goes once around in about two minutes
    private const float HueStep = 0.05f;

    private float _target;
    private float _level;
    private float _hue = 20;

    protected override void Update(MusicStats stats, int isMulti) => _target = Math.Max(stats.Band(0), stats.Band(1)) / 255.0f;

    protected override void Paint(EffectCanvas canvas)
    {
        _level = _target >= _level ? _target : Math.Max(_target, _level - 0.025f);
        _hue = (_hue + HueStep) % 360;
        var color = Hsv8.ToRgb((ushort)_hue, 230, (byte)(50 + (205 * _level)));
        canvas.FastBox(0, 0, canvas.Width - 1, canvas.Height - 1, color);
    }
}
