namespace Backbloom.Settings.Import;

/// <summary>A row of HyperHDR's <c>instances</c> table.</summary>
public sealed record LegacyInstance(int Instance, string Name, bool Enabled);

/// <summary>A row of HyperHDR's <c>settings</c> table: settings type, owning instance (null for global types) and JSON text.</summary>
public sealed record LegacySetting(string Type, int? Instance, string Config);

/// <summary>A user row of HyperHDR's <c>auth</c> table: password hash and salt as the hex text HyperHDR stores.</summary>
public sealed record LegacyUser(string Name, string PasswordHash, string Salt);

/// <summary>An API token row of HyperHDR's <c>auth</c> table: SHA-512 hex of the token.</summary>
public sealed record LegacyToken(string Id, string Comment, string TokenHash, DateTimeOffset? LastUse);

/// <summary>
/// The content of a HyperHDR (v17 to 24-beta) database or backup file, as read: nothing migrated
/// yet. Users and tokens are only present in databases (backups never contained credentials).
/// </summary>
public sealed record LegacySnapshot(
    string Source,
    string Format,
    IReadOnlyList<LegacyInstance> Instances,
    IReadOnlyList<LegacySetting> Settings,
    IReadOnlyList<LegacyUser> Users,
    IReadOnlyList<LegacyToken> Tokens,
    string? InstanceId);
