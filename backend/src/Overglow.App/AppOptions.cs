using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using Overglow.Settings;

namespace Overglow.App;

/// <summary>
/// Process options from the command line (<c>--data-dir</c>, <c>--legacy-data-dir</c>, <c>--port</c>, <c>--bind</c>,
/// <c>--ui-dir</c>, <c>--fps</c>) or <c>OVERGLOW_</c>-prefixed environment variables (<c>OVERGLOW_DATA_DIR</c> and
/// <c>OVERGLOW_LEGACY_DATA_DIR</c> as in the C++ build).
/// </summary>
/// <param name="LegacyDataDirectory">HyperHDR's data directory to import on the first start; null: none.</param>
public sealed record AppOptions(string DataDirectory, string? LegacyDataDirectory, int Port, IPAddress BindAddress, string? UiDirectory, double SyntheticFps)
{
    /// <summary>Default web port of the .NET backend while it runs next to HyperHDR (which owns 8090/8092).</summary>
    public const int DefaultPort = 18290;

    /// <summary>Value of <c>--legacy-data-dir</c> that turns the first-start import off.</summary>
    public const string NoLegacyImport = "none";

    public static AppOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var port = int.TryParse(configuration["port"], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : DefaultPort;
        var bind = IPAddress.TryParse(configuration["bind"], out var b) ? b : IPAddress.Loopback;
        var fps = double.TryParse(configuration["fps"], NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0 ? f : 60;
        var backend = FindBackendDirectory();
        return new AppOptions(
            ResolveDataDirectory(Read(configuration, "data-dir", "DATA_DIR"), backend),
            ResolveLegacyDirectory(Read(configuration, "legacy-data-dir", "LEGACY_DATA_DIR"), backend),
            port,
            bind,
            configuration["ui-dir"] is { Length: > 0 } ui ? Path.GetFullPath(ui) : FindUiDirectory(backend),
            fps);
    }

    private static string? Read(IConfiguration configuration, string option, string environment) =>
        configuration[option] is { Length: > 0 } value ? value : configuration[environment] is { Length: > 0 } env ? env : null;

    /// <summary>
    /// The per-user Overglow directory of the OS. A build that runs from a source tree uses backend/.local/data
    /// instead, so a development build can't touch an installed Overglow.
    /// </summary>
    private static string ResolveDataDirectory(string? configured, string? backend) => Path.GetFullPath(
        configured ?? (backend is null ? DataDirectories.DefaultDataDirectory() : Path.Combine(backend, ".local", "data")));

    /// <summary>HyperHDR's default directory for installed builds; a source-tree build imports only when told where from.</summary>
    private static string? ResolveLegacyDirectory(string? configured, string? backend) => configured switch
    {
        null => backend is null ? DataDirectories.DefaultLegacyDirectory() : null,
        NoLegacyImport => null,
        _ => Path.GetFullPath(configured),
    };

    /// <summary>The backend/ folder of the source tree this binary was built from, if it runs from there.</summary>
    private static string? FindBackendDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Overglow.slnx")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    private static string? FindUiDirectory(string? backend)
    {
        var repo = backend is null ? null : Directory.GetParent(backend)?.FullName;
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ui"),
            repo is null ? null : Path.Combine(repo, "ui", "dist"),
        };
        return candidates.FirstOrDefault(c => c is not null && File.Exists(Path.Combine(c, "index.html")));
    }
}
