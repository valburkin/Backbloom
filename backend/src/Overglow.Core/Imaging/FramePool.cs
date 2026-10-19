namespace Overglow.Core.Imaging;

/// <summary>
/// A small fixed set of reusable frames shared by a capture source and the pipeline. Renting and returning do not
/// allocate once the pool has created its frames; a frame is only reallocated when the capture size grows.
/// </summary>
public sealed class FramePool
{
    private readonly Lock _lock = new();
    private readonly Stack<Frame> _free;
    private readonly int _capacity;
    private int _created;

    public FramePool(int capacity = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _free = new Stack<Frame>(capacity);
    }

    /// <summary>Frames currently handed out.</summary>
    public int Rented
    {
        get
        {
            lock (_lock)
            {
                return _created - _free.Count;
            }
        }
    }

    /// <summary>Returns a frame of the given size, or null when every frame of the pool is in use.</summary>
    public Frame? TryRent(int width, int height)
    {
        Frame? frame = null;
        lock (_lock)
        {
            if (_free.Count > 0)
            {
                frame = _free.Pop();
            }
            else if (_created < _capacity)
            {
                _created++;
            }
            else
            {
                return null;
            }
        }

        if (frame is null)
        {
            return new Frame(width, height);
        }

        if (frame.Width != width || frame.Height != height)
        {
            frame.Resize(width, height);
        }

        return frame;
    }

    public void Return(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_lock)
        {
            _free.Push(frame);
        }
    }
}
