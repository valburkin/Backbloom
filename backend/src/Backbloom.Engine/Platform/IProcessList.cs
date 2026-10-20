using System.Diagnostics;

namespace Backbloom.Engine.Platform;

/// <summary>Looks for running programs by executable name. Platform projects implement it cheaply; see <see cref="PortableProcessList"/>.</summary>
public interface IProcessList
{
    /// <summary>The first of <paramref name="executableNames"/> (e.g. "hyperhdr.exe", compared case-insensitively) that runs, or null.</summary>
    string? FindRunning(IReadOnlyList<string> executableNames);
}

/// <summary>
/// Fallback for platforms without an implementation: <see cref="Process.GetProcessesByName(string)"/> (names without
/// the ".exe" extension).
/// </summary>
public sealed class PortableProcessList : IProcessList
{
    public string? FindRunning(IReadOnlyList<string> executableNames)
    {
        ArgumentNullException.ThrowIfNull(executableNames);
        foreach (var name in executableNames)
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
            var found = processes.Length > 0;
            foreach (var process in processes)
            {
                process.Dispose();
            }

            if (found)
            {
                return name;
            }
        }

        return null;
    }
}
