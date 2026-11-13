using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Backbloom.Platform.Windows.Desktop;

/// <summary>
/// A message box for errors that leave no window to show them in: a failed start or a crash. It blocks until the
/// user closes it.
/// </summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Shows a modal message box.")]
public static partial class ErrorDialog
{
    private const uint MbIconError = 0x10;
    private const uint MbSetForeground = 0x10000;
    private const uint MbTopmost = 0x40000;

    public static void Show(string title, string text)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(text);
        _ = MessageBoxW(0, text, title, MbIconError | MbSetForeground | MbTopmost);
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint owner, string text, string caption, uint type);
}
