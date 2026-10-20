using Overglow.Core.Audio;
using Overglow.Core.Color;

namespace Overglow.Core.Effects;

/// <summary>What one step of an effect produced.</summary>
public enum EffectOutput
{
    /// <summary>Nothing new (a music effect without new sound data); the LEDs keep their colors.</summary>
    None,

    /// <summary>One 8-bit sRGB color per LED.</summary>
    Leds,

    /// <summary>A picture on the <see cref="EffectCanvas"/>, mapped to the LEDs like a screen frame.</summary>
    Image,
}

/// <summary>
/// One running effect: a state machine stepped by the engine every <see cref="SleepTimeMs"/> (at most 100 ms, as
/// <c>Effect::run</c> limited it). Port of <c>AnimationBase</c> (sources/effects, MIT, awawa-dev); <see cref="Render"/>
/// is the C++ <c>Play</c> + <c>hasLedData</c> (or <c>getImage</c> for the music effects). Not thread-safe: one thread
/// steps it; a step must not allocate.
/// </summary>
public abstract class Effect
{
    /// <summary>The latch time <c>Effect::start</c> passes to <c>Init</c> (ms).</summary>
    public const int LatchTimeMs = 10;

    /// <summary>The longest time between two steps (ms).</summary>
    public const int MaxSleepTimeMs = 100;

    /// <summary>Time to the next step as the effect asks for it (ms); the engine waits at most <see cref="MaxSleepTimeMs"/>.</summary>
    public int SleepTimeMs { get; protected set; } = MaxSleepTimeMs;

    /// <summary>Prepares the effect (C++ <c>Init(hyperImage, hyperLatchTime)</c>).</summary>
    public virtual void Init(EffectCanvas canvas, int latchTimeMs)
    {
    }

    /// <summary>One step: paints <paramref name="canvas"/> or writes <paramref name="leds"/> (one entry per LED).</summary>
    public abstract EffectOutput Render(EffectCanvas canvas, Span<Rgb8> leds);
}

/// <summary>What an effect gets from the engine when it is created.</summary>
/// <param name="Random">For the effects that use <c>rand()</c>.</param>
/// <param name="Music">The sound analysis, for the music effects (null when music mode isn't available).</param>
public sealed record EffectContext(IEffectRandom Random, MusicAnalysis? Music);

/// <summary>
/// A kept effect: its name, how to create it and the smoothing it asks for (C++ <c>EffectDefinition</c>).
/// </summary>
/// <param name="SmoothingDirect">Shows its colors directly, without smoothing (the music effects).</param>
/// <param name="SmoothingTimeMs">Settling time of its own smoothing configuration.</param>
/// <param name="SmoothingFrequency">Update frequency of its own smoothing configuration; 0: the default effect one.</param>
public sealed record EffectDefinition(
    string Name,
    Func<EffectContext, Effect> Create,
    bool SmoothingDirect = false,
    int SmoothingTimeMs = 0,
    int SmoothingFrequency = 0)
{
    /// <summary>The effect has its own smoothing configuration (C++ <c>smoothingCustomSettings</c>).</summary>
    public bool HasCustomSmoothing => SmoothingFrequency != 0 || SmoothingDirect;

    /// <summary>A sound-reactive effect; its name starts with "Music: ", which the UI relies on.</summary>
    public bool IsMusic => Name.StartsWith(MusicEffect.NamePrefix, StringComparison.Ordinal);
}
