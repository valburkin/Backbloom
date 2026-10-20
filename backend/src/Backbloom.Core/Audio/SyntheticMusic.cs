namespace Backbloom.Core.Audio;

/// <summary>
/// A short synthesized dance loop for the effect previews (no recording to ship): 120 BPM, two bars that repeat (4 s),
/// with a kick on every beat, a hi-hat between them, a bass line, chords and an arpeggio. The first bar is mostly bass,
/// the second brings in the treble and gets louder, so the music's color moves from red through green to blue. Mono
/// 16-bit samples at <see cref="MusicAnalysis.SampleRate"/>; the same every time.
/// </summary>
public sealed class SyntheticMusic
{
    /// <summary>Length of the loop: two bars of four beats at 120 BPM.</summary>
    public const double LoopSeconds = 4.0;

    private const double Rate = MusicAnalysis.SampleRate;
    private const double Beat = 0.5;

    // A minor, F major, C major, G major: one chord per bar would make a 4-bar loop, so two per bar
    private static readonly double[][] s_chords =
    [
        [220.0, 261.63, 329.63],
        [174.61, 220.0, 261.63],
        [261.63, 329.63, 392.0],
        [196.0, 246.94, 293.66],
    ];

    private static readonly double[] s_bass = [55.0, 55.0, 43.65, 43.65, 65.41, 65.41, 49.0, 49.0];

    private long _sample;
    private double _kickPhase;
    private uint _noise = 1;
    private double _lastNoise;

    /// <summary>Writes the next samples.</summary>
    public void Fill(Span<short> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)Math.Clamp(Next() * 11000, short.MinValue, short.MaxValue);
        }
    }

    private double Next()
    {
        var t = (_sample++ / Rate) % LoopSeconds;
        var beat = (int)(t / Beat);
        var inBeat = t - (beat * Beat);

        // kick: a sine falling from 125 to 45 Hz
        _kickPhase += (45 + (80 * Math.Exp(-inBeat * 25))) / Rate;
        var kick = Math.Sin(2 * Math.PI * _kickPhase) * Math.Exp(-inBeat * 14);

        // hi-hat on the off-beat: high-passed noise
        _noise = unchecked((_noise * 1664525u) + 1013904223u);
        var white = ((_noise >> 8) / (double)(1 << 24)) - 0.5;
        var hiss = white - _lastNoise;
        _lastNoise = white;
        var offBeat = inBeat - (Beat / 2);
        var bright = Math.Clamp((t - 1.5) / 2.0, 0, 1);
        var hat = offBeat >= 0 ? hiss * Math.Exp(-offBeat * 50) * (0.3 + (1.5 * bright)) : 0;

        var bass = Math.Sin(2 * Math.PI * s_bass[beat] * t) * Math.Exp(-inBeat * 3) * (0.6 - (0.3 * bright));

        var chord = s_chords[beat / 2];
        var pad = 0.0;
        foreach (var f in chord)
        {
            pad += Math.Sin(2 * Math.PI * f * t) * 0.04;
        }

        // arpeggio in eighths, three octaves up, coming in during the second bar
        var eighth = (int)(t / (Beat / 2));
        var inEighth = t - (eighth * Beat / 2);
        var arp = Math.Sin(2 * Math.PI * chord[eighth % 3] * 8 * t) * Math.Exp(-inEighth * 10) * 0.3 * bright;

        var gain = 0.5 + (0.5 * t / LoopSeconds);
        return gain * ((kick * 0.9) + hat + bass + pad + arp);
    }
}
