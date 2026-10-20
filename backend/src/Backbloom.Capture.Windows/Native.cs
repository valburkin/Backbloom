using System.Runtime.Versioning;
using TerraFX.Interop.Windows;

namespace Backbloom.Capture.Windows;

/// <summary>Releases a COM pointer and clears it (SafeRelease in the C++ code).</summary>
[SupportedOSPlatform("windows10.0.17763")]
internal static unsafe class Com
{
    public static void Release<T>(ref T* pointer)
        where T : unmanaged, IUnknown.Interface
    {
        var p = pointer;
        pointer = null;
        if (p is not null)
        {
            p->Release();
        }
    }

    /// <summary>Throws <see cref="CaptureException"/> when <paramref name="hr"/> is a failure.</summary>
    public static void Check(HRESULT hr, string operation)
    {
        if (hr.FAILED)
        {
            throw new CaptureException(operation, hr);
        }
    }
}

/// <summary>A failed native call; <see cref="Exception.HResult"/> holds the HRESULT.</summary>
internal sealed class CaptureException : Exception
{
    public CaptureException(string operation, int hresult)
        : base($"{operation} failed (0x{hresult:X8})")
    {
        HResult = hresult;
    }
}
