using System.Numerics;
using Backbloom.Audio;
using Backbloom.Core.Color;
using Backbloom.Core.Effects;
using Backbloom.Core.Mapping;
using Backbloom.Engine.Platform;

namespace Backbloom.Engine;

/// <summary>
/// One running effect as a light source: steps the effect on the frame loop at the times it asks for (absolute
/// deadlines on the injected clock, at most 100 ms apart) and turns its output into linear LED colors, LED colors
/// through the sRGB table and pictures through its own image-to-LED map. Port of <c>Effect::run</c>
/// (sources/effects/Effect.cpp). The effect pauses while it isn't visible (TryRender isn't called), as in HyperHDR.
/// A music effect holds a sound capture handle until <see cref="Dispose"/>.
/// </summary>
public sealed class EffectSource : ILightSource, IDisposable
{
    // Effects wait for their next step in slices of this, so a canceled render returns quickly.
    private const long WaitSliceMs = 20;

    private readonly Effect _effect;
    private readonly IClock _clock;
    private readonly EffectCanvas _canvas = new();
    private readonly LedMapper _mapper = new();
    private readonly SoundCapture? _sound;
    private readonly int _soundHandle;
    private Rgb8[] _leds = [];
    private long _nextStep = long.MinValue / 2;
    private bool _produced;
    private int _disposed;

    public EffectSource(EffectDefinition definition, int priority, long durationMs, IClock clock, IEffectRandom random, SoundCapture? sound)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(random);
        Definition = definition;
        Priority = priority;
        DurationMs = durationMs;
        _clock = clock;
        if (definition.IsMusic && sound is not null)
        {
            _sound = sound;
            _soundHandle = sound.Open();
        }

        _effect = definition.Create(new EffectContext(random, _sound?.Analysis));
        _effect.Init(_canvas, Effect.LatchTimeMs);
    }

    public EffectDefinition Definition { get; }

    public int Priority { get; }

    /// <summary>The duration it was started with (-1: until cleared), as <c>activeEffects</c> reports it.</summary>
    public long DurationMs { get; }

    public bool TryRender(LedMappingOptions mapping, Span<Vector3> linearColors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var now = _clock.NowMilliseconds;
        if (now - _nextStep > Effect.MaxSleepTimeMs)
        {
            // first step, or visible again after a pause: start over from now
            _nextStep = now;
        }

        while (now < _nextStep)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            _clock.SleepUntil(Math.Min(_nextStep, now + WaitSliceMs));
            now = _clock.NowMilliseconds;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (_leds.Length != linearColors.Length)
        {
            _leds = new Rgb8[linearColors.Length];
        }

        var output = _effect.Render(_canvas, _leds);
        _nextStep += Math.Min(_effect.SleepTimeMs, Effect.MaxSleepTimeMs);
        switch (output)
        {
            case EffectOutput.Leds:
                var lut = Srgb.U8ToLinear16;
                for (var i = 0; i < linearColors.Length; i++)
                {
                    var c = _leds[i];
                    linearColors[i] = new Vector3(lut[c.R] / 65535.0f, lut[c.G] / 65535.0f, lut[c.B] / 65535.0f);
                }

                break;
            case EffectOutput.Image:
                // effects use no black border detection (HyperHDR disabled it for COMP_EFFECT)
                _mapper.Process(_canvas.Frame, mapping, linearColors);
                break;
            default:
                if (_produced)
                {
                    return false;
                }

                // a music effect without sound yet starts dark instead of keeping the previous source's colors
                linearColors.Clear();
                break;
        }

        _produced = true;
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _sound is not null)
        {
            _sound.Close(_soundHandle);
        }
    }
}
