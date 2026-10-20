using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Backbloom.Core.Tests.Golden;

/// <summary>
/// Compares values against a golden fixture and collects every mismatch, so a failure shows how many values differ
/// and a table of the first ones (where, expected, actual, difference, tolerance) instead of stopping at the first.
/// </summary>
internal sealed class GoldenDiff(string fixture, float tolerance)
{
    private const int MaxListed = 20;
    private readonly List<string> _mismatches = [];
    private int _compared;

    public float Tolerance { get; } = tolerance;

    public void Compare(string where, float expected, float actual)
    {
        _compared++;
        var difference = MathF.Abs(expected - actual);
        if (!(difference <= Tolerance) && !(float.IsNaN(expected) && float.IsNaN(actual)))
        {
            _mismatches.Add($"{where}: expected {Format(expected)}, actual {Format(actual)}, |diff| {Format(difference)}");
        }
    }

    public void Compare(string where, Vector3 expected, Vector3 actual)
    {
        Compare($"{where} R", expected.X, actual.X);
        Compare($"{where} G", expected.Y, actual.Y);
        Compare($"{where} B", expected.Z, actual.Z);
    }

    public void Equal<T>(string where, T expected, T actual)
    {
        _compared++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            _mismatches.Add($"{where}: expected {expected}, actual {actual}");
        }
    }

    /// <summary>Compares two byte strings (hex in the fixture) and lists the differing bytes.</summary>
    public void Bytes(string where, ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, int bytesPerItem = 3)
    {
        Equal($"{where} length", expected.Length, actual.Length);
        for (var i = 0; i < Math.Min(expected.Length, actual.Length); i++)
        {
            Equal($"{where} item {i / bytesPerItem} byte {i % bytesPerItem}", expected[i], actual[i]);
        }
    }

    /// <summary>Fails the test when anything differed.</summary>
    public void AssertNoMismatches()
    {
        if (_mismatches.Count == 0)
        {
            return;
        }

        var message = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{fixture}: {_mismatches.Count} of {_compared} values differ from the C++ reference ")
            .Append(CultureInfo.InvariantCulture, $"(tolerance {Format(Tolerance)}). First {Math.Min(MaxListed, _mismatches.Count)}:")
            .AppendLine();
        foreach (var line in _mismatches.Take(MaxListed))
        {
            message.Append("  ").AppendLine(line);
        }

        Assert.Fail(message.ToString());
    }

    public static Vector3 Vector(JsonNode? node)
    {
        var a = node?.AsArray() ?? throw new InvalidDataException("Expected [r, g, b].");
        return new Vector3(a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>());
    }

    public static Vector3[] Vectors(JsonNode? node) => [.. node!.AsArray().Select(Vector)];

    // "R" round-trips: the printed value is the exact float
    private static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}
