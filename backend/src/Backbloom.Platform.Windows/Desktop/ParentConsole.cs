using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Backbloom.Platform.Windows.Desktop;

/// <summary>
/// backbloom.exe is a GUI program (no console window of its own when started from Explorer, autostart or the tray).
/// Started from a terminal it attaches to that terminal's console, so <c>--help</c>, <c>--version</c> and the log
/// of <c>--service</c> show there. Output redirected to a file or pipe works without attaching.
/// </summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Attaches the process to the console of the program that started it, which would take over the test runner's console.")]
public static partial class ParentConsole
{
    private const uint AttachParentProcess = unchecked((uint)-1);

    /// <summary>True when the process now writes to a console or a redirected output.</summary>
    public static bool Attach()
    {
        if (Console.IsOutputRedirected)
        {
            return true;
        }

        return GetConsoleWindow() != 0 || AttachConsole(AttachParentProcess) != 0;
    }

    [LibraryImport("kernel32.dll")]
    private static partial int AttachConsole(uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();
}
