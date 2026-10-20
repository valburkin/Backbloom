using Overglow.Engine.Platform;

namespace Overglow.Tests.Platform;

/// <summary>
/// A process list the tests control: test hosts use it so the real HyperHDR running on a development machine doesn't
/// hold their LEDs (see <c>OtherAppMonitor</c>).
/// </summary>
public sealed class FakeProcessList : IProcessList
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);

    public int Calls { get; private set; }

    public void Start(string executable)
    {
        lock (_lock)
        {
            _running.Add(executable);
        }
    }

    public void Stop(string executable)
    {
        lock (_lock)
        {
            _running.Remove(executable);
        }
    }

    public string? FindRunning(IReadOnlyList<string> executableNames)
    {
        lock (_lock)
        {
            Calls++;
            return executableNames.FirstOrDefault(_running.Contains);
        }
    }
}
