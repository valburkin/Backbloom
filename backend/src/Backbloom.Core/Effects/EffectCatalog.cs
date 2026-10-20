using Backbloom.Core.Color;

namespace Backbloom.Core.Effects;

/// <summary>
/// The ten effects this build keeps, with the parameters and smoothing of their C++ presets (the
/// <c>REGISTER_EFFECT</c> calls in sources/effects), and the music effects new in Backbloom (<see cref="MusicAnimation"/>).
/// Settings imported from HyperHDR map removed effects to these (see LegacyMigrations).
/// </summary>
public static class EffectCatalog
{
    public static IReadOnlyList<EffectDefinition> All { get; } =
    [
        new("Rainbow swirl", _ => new SwirlEffect(20.0), SmoothingTimeMs: 200, SmoothingFrequency: 25,
            Description: "A rainbow slowly circles around the screen."),
        new("Rainbow swirl fast", _ => new SwirlEffect(7.0),
            Description: "The rainbow swirl at a lively pace."),
        new("Full color mood blobs", c => new MoodBlobsEffect(new MoodBlobsEffect.Parameters(new Rgb8(0, 0, 255), true, 0, 360, 0.2), c.Random),
            Description: "Soft blobs of every color drift around the frame."),
        new("Warm mood blobs", c => new MoodBlobsEffect(new MoodBlobsEffect.Parameters(new Rgb8(255, 0, 0), false, 333, 151, 2.0), c.Random), SmoothingTimeMs: 200, SmoothingFrequency: 25,
            Description: "Soft blobs in reds and oranges drift around the frame."),
        new("Breath", _ => new FadeEffect(new FadeEffect.Parameters(new Rgb8(50, 50, 50), new Rgb8(255, 255, 255), 3000, 1000, 50, 250)),
            Description: "White light slowly brightens and dims, like breathing."),
        new("Candle", c => new CandleEffect(c.Random), SmoothingTimeMs: 500, SmoothingFrequency: 20,
            Description: "A warm flicker, like candlelight."),
        new(MusicPulseEffect.Name, c => new MusicPulseEffect(c.Music), SmoothingDirect: true,
            Description: "The whole frame pulses in the music's color."),
        new(MusicQuatroEffect.Name, c => new MusicQuatroEffect(c.Music), SmoothingDirect: true,
            Description: "Bars grow from the middle of all four sides with the volume."),
        new(MusicStereoEffect.Name, c => new MusicStereoEffect(c.Music), SmoothingDirect: true,
            Description: "Bars grow from the middle of the left and right sides with the volume."),
        new(MusicWavesEffect.Name, c => new MusicWavesEffect(c.Music), SmoothingDirect: true,
            Description: "The latest colors run as waves from the middle of each side to the corners."),
        new(MusicSpectrumEffect.Name, c => new MusicSpectrumEffect(c.Music), SmoothingDirect: true,
            Description: "Every frequency in its own color: bass at the bottom, treble at the top."),
        new(MusicVuMeterEffect.Name, c => new MusicVuMeterEffect(c.Music), SmoothingDirect: true,
            Description: "Both sides fill up with the volume, from green to red, with a falling peak."),
        new(MusicBeatRippleEffect.Name, c => new MusicBeatRippleEffect(c.Music), SmoothingDirect: true,
            Description: "Every beat sends a ring of color out from the middle of the screen."),
        new(MusicWaterfallEffect.Name, c => new MusicWaterfallEffect(c.Music), SmoothingDirect: true,
            Description: "The music's colors rise from the bottom up both sides."),
        new(MusicBassGlowEffect.Name, c => new MusicBassGlowEffect(c.Music), SmoothingDirect: true,
            Description: "A calm glow in slowly changing colors that swells with the bass."),
    ];

    /// <summary>The effect named <paramref name="name"/> (case-sensitive, as in C++), or null.</summary>
    public static EffectDefinition? Find(string? name) => All.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
}
