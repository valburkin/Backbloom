using System.Text.Json.Nodes;

namespace Overglow.Settings;

/// <summary>Outcome of <see cref="ISettingsStore.Update"/> and <see cref="ISettingsStore.Replace"/>.</summary>
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

/// <summary>
/// One LED controller fed by the pipeline. HyperHDR's instances map to controllers (see the README): the first
/// controller (id 0) is the one the <c>device</c> section of <c>getconfig</c>/<c>setconfig</c> refers to.
/// </summary>
public sealed record ControllerSettings(int Id, string Name, bool Enabled, JsonObject Device);

/// <summary>Everything the store holds except credentials: the pipeline's sections (no <c>device</c>) and the controllers.</summary>
public sealed record SettingsContent(JsonObject Sections, IReadOnlyList<ControllerSettings> Controllers);

/// <summary>Validated, persisted settings, one JSON value per section.</summary>
public interface ISettingsStore
{
    /// <summary>Raised after a successful update that changed at least one section (on the updating thread).</summary>
    event Action<SettingsChange>? Changed;

    /// <summary>Deep copy of every section that has a value; <c>device</c> is the first controller's.</summary>
    JsonObject Snapshot();

    /// <summary>Deep copy of one section, or null when it has no value.</summary>
    JsonNode? GetSection(string section);

    /// <summary>Deep copies of every controller, by id.</summary>
    IReadOnlyList<ControllerSettings> Controllers { get; }

    /// <summary>
    /// Replaces the given top-level sections and keeps the others, in one transaction. Every section is validated
    /// first; when anything is invalid nothing is stored and every problem is returned.
    /// </summary>
    SettingsUpdateResult Update(JsonObject sections);

    /// <summary>Deep copy of everything (for backups).</summary>
    SettingsContent Export();

    /// <summary>
    /// Replaces everything (restore, import) in one transaction: sections missing from <paramref name="content"/>
    /// get their defaults. Nothing is stored when anything is invalid.
    /// </summary>
    SettingsUpdateResult Replace(SettingsContent content);
}
