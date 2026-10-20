using System.Runtime.Versioning;
using Microsoft.Win32;
using Backbloom.Engine.Platform;

namespace Backbloom.Platform.Windows.Desktop;

/// <summary>
/// "Start with Windows": a value in the current user's Run key (port of SystrayHandler's autorun: value
/// <c>Backbloom</c> under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>). The entry starts the app in the
/// tray (<c>--no-window</c>), with the data directory when it isn't the default one. It counts as enabled only when it
/// starts this executable. The key is a parameter so tests use a scratch key and never touch the real one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryAutostart : IAutostart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Backbloom";

    private readonly RegistryKey _root;
    private readonly string _keyPath;
    private readonly string _executable;
    private readonly IReadOnlyList<string> _arguments;

    /// <param name="root">Registry hive (the app: <see cref="Registry.CurrentUser"/>).</param>
    /// <param name="keyPath">Key under <paramref name="root"/> (the app: <see cref="RunKey"/>).</param>
    /// <param name="executable">The program to start (the app: its own path).</param>
    /// <param name="arguments">Its arguments.</param>
    public RegistryAutostart(RegistryKey root, string keyPath, string executable, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrEmpty(keyPath);
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        _root = root;
        _keyPath = keyPath;
        _executable = Path.GetFullPath(executable);
        _arguments = arguments;
    }

    public string Label => "Start with Windows";

    /// <summary>The command line written to the Run key.</summary>
    public string Command => string.Join(' ', new[] { _executable }.Concat(_arguments).Select(Quote));

    public bool IsEnabled
    {
        get
        {
            using var key = _root.OpenSubKey(_keyPath);
            return key?.GetValue(ValueName) is string command && StartsThisExecutable(command);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = _root.CreateSubKey(_keyPath, writable: true);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = _root.OpenSubKey(_keyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>The executable of a Run command (quoted or not) is this one.</summary>
    internal bool StartsThisExecutable(string command)
    {
        var trimmed = command.Trim();
        string program;
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            program = end < 0 ? trimmed[1..] : trimmed[1..end];
        }
        else
        {
            // C++ wrote the bare path; it may contain spaces, so compare the start
            return trimmed.Equals(_executable, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(_executable + " ", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(program, _executable, StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"') ? argument : $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
