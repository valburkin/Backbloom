using System.Globalization;
using System.Net;
using System.Text;
using Backbloom.App.Updates;

namespace Backbloom.App;

/// <summary>What the process was asked to do.</summary>
internal enum LaunchMode
{
    /// <summary>Run Backbloom (tray and window unless <c>--service</c>).</summary>
    Run,

    /// <summary><c>--import &lt;file&gt;</c>: import a database into the data directory and exit.</summary>
    Import,

    /// <summary><c>--quit</c>: ask the running instance of the data directory to quit, wait for it, exit.</summary>
    Quit,

    Version,

    Help,
}

/// <summary>
/// The command line, validated: unknown options and bad values are errors (exit code 2) instead of being ignored.
/// Settings options become configuration keys for <see cref="AppOptions"/> (which also reads <c>BACKBLOOM_*</c>
/// environment variables); <c>--name value</c> and <c>--name=value</c> are both accepted.
/// </summary>
internal sealed record CommandLine(LaunchMode Mode, IReadOnlyList<string> ConfigurationArgs, string? ImportFile, bool NoWindow, bool Service)
{
    private static readonly string[] s_settings = ["data-dir", "legacy-data-dir", "port", "bind", "ui-dir", "capture", "capture-image", "fps", "lut-dirs", "update-url", "update-channel"];

    public static string Usage => """
        Usage: backbloom [options]

          --data-dir <dir>          Settings, logs and window state (default: the per-user Backbloom folder)
          --legacy-data-dir <dir>   HyperHDR's data folder to import on the first start ("none": don't)
          --port <port>             Web port: UI, WebSocket and HTTP JSON-RPC (default 18290)
          --bind <address>          The one address to listen on (default: 127.0.0.1, and the local network
                                    only while remote access is on in Settings)
          --ui-dir <dir>            Built web UI to serve (default: ui next to the program, or ui/dist)
          --capture screen|synthetic  Frame source (default screen)
          --capture-image <file>    Use a still picture (binary PPM) as the frame source instead (screenshots)
          --fps <n>                 Rate of the synthetic test pattern or the picture (default 60)
          --lut-dirs <dirs>         Folders searched for the HDR tone mapping table, separated by ';'
          --update-url <url>|none   Release feed for automatic updates of an installed copy
                                    (default: the GitHub releases); none turns them off
          --update-channel <name>   Release channel to follow (default: the installed one, e.g. stable)
          --no-window               Start in the tray without opening the window (used by autostart)
          --service                 No tray icon or window; logs to the console (headless use)
          --import <file>           Import a HyperHDR or Backbloom database into the data folder and exit
          --quit                    Ask the running Backbloom (same data folder) to quit, then exit
          --version                 Show the version and exit
          -h, --help                Show this help and exit

        A second start with the same data folder opens the running one's window instead.
        """;

    /// <returns>The command line, or the error to show.</returns>
    public static (CommandLine? CommandLine, string? Error) Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var configuration = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? import = null;
        bool noWindow = false, service = false, quit = false, version = false, help = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "-?" or "/?")
            {
                help = true;
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                return (null, $"Unexpected argument '{arg}'.");
            }

            var equals = arg.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? arg[2..] : arg[2..equals];
            string? inline = equals < 0 ? null : arg[(equals + 1)..];
            if (!seen.Add(name))
            {
                return (null, $"--{name} is given twice.");
            }

            switch (name)
            {
                case "no-window":
                case "service":
                case "quit":
                case "version":
                case "help":
                    if (inline is not null)
                    {
                        return (null, $"--{name} takes no value.");
                    }

                    noWindow |= name == "no-window";
                    service |= name == "service";
                    quit |= name == "quit";
                    version |= name == "version";
                    help |= name == "help";
                    continue;
            }

            if (name != "import" && !s_settings.Contains(name))
            {
                return (null, $"Unknown option --{name}.");
            }

            var value = inline ?? (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null);
            if (string.IsNullOrWhiteSpace(value))
            {
                return (null, $"--{name} needs a value.");
            }

            if (Validate(name, value) is { } error)
            {
                return (null, error);
            }

            if (name == "import")
            {
                import = value;
            }
            else
            {
                configuration.Add($"--{name}");
                configuration.Add(value);
            }
        }

        var modes = (import is not null ? 1 : 0) + (quit ? 1 : 0) + (version ? 1 : 0) + (help ? 1 : 0);
        if (modes > 1)
        {
            return (null, "Use only one of --import, --quit, --version and --help.");
        }

        if (noWindow && service)
        {
            return (null, "--no-window and --service can't be combined (--service has no window anyway).");
        }

        var mode = help ? LaunchMode.Help : version ? LaunchMode.Version : quit ? LaunchMode.Quit : import is not null ? LaunchMode.Import : LaunchMode.Run;
        return (new CommandLine(mode, configuration, import, noWindow, service), null);
    }

    /// <summary>Text for <c>--version</c>.</summary>
    public static string VersionText(string version, string build)
    {
        var text = new StringBuilder();
        text.Append("Backbloom ").Append(version).Append(" (").Append(build).AppendLine(")");
        text.Append(".NET ").Append(Environment.Version.ToString()).Append(", ").AppendLine(System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        return text.ToString();
    }

    private static string? Validate(string name, string value) => name switch
    {
        "port" when !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > 65535 =>
            $"--port must be a number from 0 to 65535, not '{value}'.",
        "bind" when !IPAddress.TryParse(value, out _) => $"--bind must be an IP address, not '{value}'.",
        "capture" when !Enum.TryParse<CaptureKind>(value, ignoreCase: true, out var kind) || kind is not (CaptureKind.Screen or CaptureKind.Synthetic) || int.TryParse(value, out _) =>
            $"--capture must be 'screen' or 'synthetic', not '{value}'.",
        "capture-image" when !File.Exists(value) => $"--capture-image: there is no file '{value}'.",
        "update-url" when !value.Equals(UpdateSettings.Off, StringComparison.OrdinalIgnoreCase)
            && (!Uri.TryCreate(value, UriKind.Absolute, out var feed) || feed.Scheme is not ("https" or "http" or "file")) =>
            $"--update-url must be an absolute http(s) or file URL, or '{UpdateSettings.Off}', not '{value}'.",
        "fps" when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) || fps is <= 0 or > 1000 =>
            $"--fps must be a number above 0 and up to 1000, not '{value}'.",
        _ => null,
    };
}
