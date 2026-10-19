namespace Overglow.Settings;

/// <summary>
/// Default locations of this product's data and of HyperHDR's (port of LegacyMigration's path rules). Pure: nothing
/// here creates or reads a directory except the existence check of HyperHDR's home folder.
/// </summary>
public static class DataDirectories
{
    public const string ProductDirectoryName = "Overglow";

    private const string LegacyAppDirectoryName = "HyperHDR";
    private const string LegacyHomeDirectoryName = ".hyperhdr";

    /// <summary>Where the HyperHDR database lives inside its data directory.</summary>
    public static readonly string LegacyDatabase = Path.Combine("db", "hyperhdr.db");

    /// <summary>Where the Overglow C++ build kept its database inside the (same) data directory.</summary>
    public static readonly string CppDatabase = Path.Combine("db", "overglow.db");

    /// <summary>
    /// The per-user data directory: <c>%LOCALAPPDATA%\Overglow</c> on Windows, <c>~/Library/Application Support/Overglow</c>
    /// on macOS, <c>$XDG_CONFIG_HOME/Overglow</c> (default <c>~/.config/Overglow</c>) elsewhere.
    /// </summary>
    public static string DefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductDirectoryName);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", ProductDirectoryName);
        }

        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(config) ? Path.Combine(home, ".config") : config, ProductDirectoryName);
    }

    /// <summary>
    /// HyperHDR's default data directory: <c>~/.hyperhdr</c> when it exists, else Qt's AppConfigLocation for
    /// HyperHDR (<c>%LOCALAPPDATA%\HyperHDR</c>, <c>~/Library/Preferences/HyperHDR</c>, <c>~/.config/HyperHDR</c>).
    /// </summary>
    public static string DefaultLegacyDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var legacyHome = Path.Combine(home, LegacyHomeDirectoryName);
        if (Directory.Exists(legacyHome))
        {
            return legacyHome;
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyAppDirectoryName);
        }

        return OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Preferences", LegacyAppDirectoryName)
            : Path.Combine(home, ".config", LegacyAppDirectoryName);
    }
}
