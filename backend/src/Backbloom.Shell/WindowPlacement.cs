using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backbloom.Shell;

/// <summary>Where the window was, in screen pixels of its normal (not maximized) state.</summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized)
{
    /// <summary>Smallest size that is restored; anything smaller is treated as broken.</summary>
    public const int MinimumSize = 200;

    [JsonIgnore]
    public bool IsUsable => Width >= MinimumSize && Height >= MinimumSize;
}

/// <summary>
/// Keeps the window placement in <c>window.json</c> of the data directory. Written to a temporary file and moved
/// over the old one, so a crash never leaves a half-written file; unreadable content counts as "no placement".
/// </summary>
public sealed class WindowPlacementStore(string dataDirectory)
{
    public const string FileName = "window.json";

    public string Path { get; } = System.IO.Path.Combine(dataDirectory, FileName);

    public WindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            var placement = JsonSerializer.Deserialize(File.ReadAllBytes(Path), WindowPlacementJson.Default.WindowPlacement);
            return placement is { IsUsable: true } ? placement : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <returns>False when the file couldn't be written (the placement is then just not remembered).</returns>
    public bool Save(WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var temporary = Path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(placement, WindowPlacementJson.Default.WindowPlacement));
            File.Move(temporary, Path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

[JsonSerializable(typeof(WindowPlacement))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class WindowPlacementJson : JsonSerializerContext;
