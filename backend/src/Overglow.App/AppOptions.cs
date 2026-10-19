using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace Overglow.App;

/// <summary>
/// Process options from the command line (<c>--data-dir</c>, <c>--port</c>, <c>--bind</c>, <c>--ui-dir</c>,
/// <c>--fps</c>) or <c>OVERGLOW_</c>-prefixed environment variables.
/// </summary>
public sealed record AppOptions(string DataDirectory, int Port, IPAddress BindAddress, string? UiDirectory, double SyntheticFps)
{
    /// <summary>Default web port of the .NET backend while it runs next to HyperHDR (which owns 8090/8092).</summary>
    public const int DefaultPort = 18290;

    public static AppOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var dataDir = configuration["data-dir"];
        var port = int.TryParse(configuration["port"], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : DefaultPort;
        var bind = IPAddress.TryParse(configuration["bind"], out var b) ? b : IPAddress.Loopback;
        var fps = double.TryParse(configuration["fps"], NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0 ? f : 60;
        var backend = FindBackendDirectory();
        return new AppOptions(
            Path.GetFullPath(string.IsNullOrWhiteSpace(dataDir) ? DefaultDataDirectory(backend) : dataDir),
            port,
            bind,
            configuration["ui-dir"] is { Length: > 0 } ui ? Path.GetFullPath(ui) : FindUiDirectory(backend),
            fps);
    }

    /// <summary>
    /// During development the data lives in backend/.local/data of the source tree, never in the user's profile, so a
    /// development build can't touch an installed Overglow or HyperHDR.
    /// </summary>
    private static string DefaultDataDirectory(string? backend) =>
        Path.Combine(backend ?? AppContext.BaseDirectory, ".local", "data");

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
