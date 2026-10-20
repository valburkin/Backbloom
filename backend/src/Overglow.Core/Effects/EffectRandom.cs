namespace Overglow.Core.Effects;

/// <summary>The random numbers of an effect ("Candle", the start hue of "Full color mood blobs").</summary>
public interface IEffectRandom
{
    /// <summary>The next number in 0..<see cref="MsvcRandom.Max"/> (like C <c>rand()</c>).</summary>
    int Rand();
}

/// <summary>
/// The linear congruential generator of the Microsoft C runtime's <c>rand()</c>: state = state * 214013 + 2531011
/// (mod 2^32), result = (state &gt;&gt; 16) &amp; 0x7fff, <c>RAND_MAX</c> = 32767; <c>srand(seed)</c> sets the state.
/// The C++ effects called <c>rand()</c>, so with the same seed this reproduces their output exactly (the golden
/// fixtures use seed 42). HyperHDR never seeded it, and the CRT state is per thread, so every effect thread started
/// from seed 1 and "Candle" flickered the same way every time; the engine seeds each effect from
/// <see cref="Random.Shared"/> instead. Not thread-safe: one instance per effect.
/// </summary>
public sealed class MsvcRandom(uint seed) : IEffectRandom
{
    /// <summary>RAND_MAX of the Microsoft C runtime.</summary>
    public const int Max = 0x7fff;

    private uint _state = seed;

    /// <summary>A generator with a seed from <see cref="Random.Shared"/>.</summary>
    public static MsvcRandom CreateSeeded() => new((uint)Random.Shared.Next());

    public int Rand()
    {
        _state = unchecked((_state * 214013u) + 2531011u);
        return (int)((_state >> 16) & 0x7fff);
    }
}
