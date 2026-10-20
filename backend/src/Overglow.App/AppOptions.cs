using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using Overglow.Settings;

namespace Overglow.App;

/// <summary>Which frame source drives the lighting.</summary>
public enum CaptureKind
{
    /// <summary>Screen capture (DXGI desktop duplication on Windows), configured by the <c>systemGrabber</c> settings.</summary>
    Screen,

    /// <summary>The synthetic test pattern (development, tests, platforms without capture).</summary>
    Synthetic,
}

/// <summary>
/// Process options from the command line (<c>--data-dir</c>, <c>--legacy-data-dir</c>, <c>--port</c>, <c>--bind</c>,
/// <c>--ui-dir</c>, <c>--capture</c>, <c>--fps</c>, <c>--lut-dirs</c>) or <c>OVERGLOW_</c>-prefixed environment variables
/// (<c>OVERGLOW_DATA_DIR</c> and <c>OVERGLOW_LEGACY_DATA_DIR</c> as in the C++ build).
/// </summary>
/// <param name="LegacyDataDirectory">HyperHDR's data directory to import on the first start; null: none.</param>
/// <param name="BindAddress">
/// <c>--bind</c>: the one address to listen on, which overrides the <c>network.remoteAccess</c> setting; null (the
/// default): loopback, plus every interface while remote access is on (see <see cref="RemoteAccess"/>).
/// </param>
/// <param name="Capture">Frame source: screen capture (default) or the synthetic test pattern.</param>
/// <param name="SyntheticFps">Rate of the synthetic test pattern.</param>
/// <param name="LutDirectories">Directories searched for <c>lut_lin_tables.3d</c>, in order; the default table is created in the first.</param>
public sealed record AppOptions(
    string DataDirectory,
    string? LegacyDataDirectory,
    int Port,
    IPAddress? BindAddress,
    string? UiDirectory,
    CaptureKind Capture,
    double SyntheticFps,
    IReadOnlyList<string> LutDirectories)
{
    /// <summary>Default web port of the .NET backend while it runs next to HyperHDR (which owns 8090/8092).</summary>
    public const int DefaultPort = 18290;

    /// <summary>Value of <c>--legacy-data-dir</c> that turns the first-start import off.</summary>
    public const string NoLegacyImport = "none";

    public static AppOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var port = int.TryParse(configuration["port"], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : DefaultPort;
        var bind = IPAddress.TryParse(Read(configuration, "bind", "BIND"), out var b) ? b : null;
        var capture = Enum.TryParse<CaptureKind>(configuration["capture"], ignoreCase: true, out var c) ? c : CaptureKind.Screen;
        var fps = double.TryParse(configuration["fps"], NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0 ? f : 60;
        var backend = FindBackendDirectory();
        var data = ResolveDataDirectory(Read(configuration, "data-dir", "DATA_DIR"), backend);

        // HyperHDR's policy: the data directory, then the application directory.
        var lutDirs = configuration["lut-dirs"] is { Length: > 0 } dirs
            ? dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFullPath).ToArray()
            : [data, AppContext.BaseDirectory];
        return new AppOptions(
            data,
            ResolveLegacyDirectory(Read(configuration, "legacy-data-dir", "LEGACY_DATA_DIR"), backend),
            port,
            bind,
            configuration["ui-dir"] is { Length: > 0 } ui ? Path.GetFullPath(ui) : FindUiDirectory(backend),
            capture,
            fps,
            lutDirs);
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
