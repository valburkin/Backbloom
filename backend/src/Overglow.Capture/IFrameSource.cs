using Overglow.Core.Imaging;

namespace Overglow.Capture;

/// <summary>
/// Something that produces frames: screen capture, video, or a test pattern. A source paces itself:
/// <see cref="TryCapture"/> blocks until the next frame is available.
/// </summary>
public interface IFrameSource : IDisposable
{
    /// <summary>Name shown as the grabber "owner" in the UI (e.g. "DirectX11" or "Synthetic test pattern").</summary>
    string Name { get; }

    /// <summary>Display/mode description for <c>serverinfo.systemGrabbers</c>.</summary>
    string Device { get; }

    /// <summary>Size of the frames this source produces now.</summary>
    (int Width, int Height) FrameSize { get; }

    /// <summary>
    /// Waits for the next frame and writes it into <paramref name="frame"/> (already sized to <see cref="FrameSize"/>).
    /// Returns false when no frame arrived (timeout, source lost) or <paramref name="cancellationToken"/> was canceled.
    /// Must not allocate in steady state.
    /// </summary>
    bool TryCapture(Frame frame, CancellationToken cancellationToken);
}
