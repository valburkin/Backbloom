using Overglow.Core.Color;

namespace Overglow.Core.Effects;

/// <summary>
/// The ten effects this build keeps, with the parameters and smoothing of their C++ presets (the
/// <c>REGISTER_EFFECT</c> calls in sources/effects). Settings imported from HyperHDR map removed effects to these
/// (see LegacyMigrations).
/// </summary>
public static class EffectCatalog
{
    public static IReadOnlyList<EffectDefinition> All { get; } =
    [
        new("Rainbow swirl", _ => new SwirlEffect(20.0), SmoothingTimeMs: 200, SmoothingFrequency: 25),
        new("Rainbow swirl fast", _ => new SwirlEffect(7.0)),
        new("Full color mood blobs", c => new MoodBlobsEffect(new MoodBlobsEffect.Parameters(new Rgb8(0, 0, 255), true, 0, 360, 0.2), c.Random)),
        new("Warm mood blobs", c => new MoodBlobsEffect(new MoodBlobsEffect.Parameters(new Rgb8(255, 0, 0), false, 333, 151, 2.0), c.Random), SmoothingTimeMs: 200, SmoothingFrequency: 25),
        new("Breath", _ => new FadeEffect(new FadeEffect.Parameters(new Rgb8(50, 50, 50), new Rgb8(255, 255, 255), 3000, 1000, 50, 250))),
        new("Candle", c => new CandleEffect(c.Random), SmoothingTimeMs: 500, SmoothingFrequency: 20),
        new(MusicPulseEffect.Name, c => new MusicPulseEffect(c.Music), SmoothingDirect: true),
        new(MusicQuatroEffect.Name, c => new MusicQuatroEffect(c.Music), SmoothingDirect: true),
        new(MusicStereoEffect.Name, c => new MusicStereoEffect(c.Music), SmoothingDirect: true),
        new(MusicWavesEffect.Name, c => new MusicWavesEffect(c.Music), SmoothingDirect: true),
    ];

    /// <summary>The effect named <paramref name="name"/> (case-sensitive, as in C++), or null.</summary>
    public static EffectDefinition? Find(string? name) => All.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
}
