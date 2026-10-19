using System.Text.Json.Nodes;

namespace Overglow.Settings;

/// <summary>Outcome of <see cref="ISettingsStore.Update"/>.</summary>
public sealed record SettingsUpdateResult(IReadOnlyList<SettingsError> Errors, IReadOnlyList<string> ChangedSections)
{
    public bool Success => Errors.Count == 0;

    public static SettingsUpdateResult Rejected(IReadOnlyList<SettingsError> errors) => new(errors, []);
}

/// <summary>Sections that changed in one update, with their new values.</summary>
public sealed record SettingsChange(IReadOnlyDictionary<string, JsonNode> Sections)
{
    public bool Contains(string section) => Sections.ContainsKey(section);
}

/// <summary>Validated, persisted settings, one JSON value per section.</summary>
public interface ISettingsStore
{
    /// <summary>Raised after a successful update that changed at least one section (on the updating thread).</summary>
    event Action<SettingsChange>? Changed;

    /// <summary>Deep copy of every section that has a value.</summary>
    JsonObject Snapshot();

    /// <summary>Deep copy of one section, or null when it has no value.</summary>
    JsonNode? GetSection(string section);

    /// <summary>
    /// Replaces the given top-level sections and keeps the others. Every section is validated first; when anything is
    /// invalid nothing is stored and every problem is returned.
    /// </summary>
    SettingsUpdateResult Update(JsonObject sections);
}
