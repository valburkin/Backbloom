using System.Diagnostics;

namespace Backbloom.Engine.Platform;

/// <summary>A running program with a window, as the profile editor's program picker lists it.</summary>
/// <param name="Executable">Its executable name, e.g. "witcher3.exe" (what a profile's <c>apps</c> hold).</param>
/// <param name="Title">The title of one of its windows.</param>
public sealed record RunningProgram(string Executable, string Title);

/// <summary>Lists the programs that have a window open. Platform projects implement it; see <see cref="PortableProgramList"/>.</summary>
public interface IProgramList
{
    /// <summary>
    /// Programs with a visible window other than this app, one entry per executable (compared case-insensitively),
    /// sorted by executable name.
    /// </summary>
    IReadOnlyList<RunningProgram> WindowedPrograms();
}

/// <summary>
/// Fallback for platforms without an implementation: processes with a main window title
/// (<see cref="Process.MainWindowTitle"/>; on other systems than Windows usually none), named as the process list
/// names them.
/// </summary>
public sealed class PortableProgramList : IProgramList
{
    public IReadOnlyList<RunningProgram> WindowedPrograms()
    {
        var own = Environment.ProcessId;
        var programs = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var title = process.MainWindowTitle;
                    if (process.Id != own && !string.IsNullOrWhiteSpace(title))
                    {
                        var name = OperatingSystem.IsWindows() ? process.ProcessName + ".exe" : process.ProcessName;
                        programs.TryAdd(name, new RunningProgram(name, title));
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    // the process ended meanwhile, or this one can't be asked
                }
            }
        }

        return [.. programs.Values.OrderBy(p => p.Executable, StringComparer.OrdinalIgnoreCase)];
    }
}
