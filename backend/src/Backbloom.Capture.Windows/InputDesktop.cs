using System.Runtime.Versioning;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Backbloom.Capture.Windows;

/// <summary>
/// Attaches the capture thread to the desktop that receives user input. Desktop duplication only works on the input
/// desktop; when Windows switches to the secure desktop (UAC prompt, lock screen) the duplication is lost and a
/// process that may open that desktop (a service) has to follow it. A normal user process can't, so duplication fails
/// with E_ACCESSDENIED until the user is back and capture resumes on its own.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed unsafe class InputDesktop : IDisposable
{
    private const uint GenericAll = 0x10000000;

    private HDESK _attached;

    /// <summary>Switches the calling thread to the current input desktop; false when it can't be opened.</summary>
    public bool AttachCurrentThread()
    {
        var desktop = OpenInputDesktop(0, FALSE, GenericAll);
        if (desktop == HDESK.NULL)
        {
            return false;
        }

        if (SetThreadDesktop(desktop) == FALSE)
        {
            CloseDesktop(desktop);
            return false;
        }

        if (_attached != HDESK.NULL)
        {
            CloseDesktop(_attached);
        }

        _attached = desktop;
        return true;
    }

    /// <summary>
    /// Makes the calling thread per-monitor DPI aware (v2). IDXGIOutput5::DuplicateOutput1, which HDR capture needs,
    /// fails with DXGI_ERROR_UNSUPPORTED for threads that aren't.
    /// </summary>
    public static void MakeThreadDpiAware() => SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    public void Dispose()
    {
        // The handle stays valid while the thread uses the desktop; closing fails harmlessly in that case.
        if (_attached != HDESK.NULL)
        {
            CloseDesktop(_attached);
            _attached = HDESK.NULL;
        }
    }
}
