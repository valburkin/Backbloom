using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Backbloom.Shell;

/// <summary>What a second launch asks the running instance.</summary>
public enum InstanceRequest
{
    /// <summary>Show the UI (a second start of the app).</summary>
    Activate,

    /// <summary>Quit (<c>--quit</c>, e.g. before an update).</summary>
    Quit,
}

/// <summary>The running instance's answer.</summary>
/// <param name="Url">For <see cref="InstanceRequest.Activate"/> when the instance has no window (<c>--service</c>): the UI to open in a browser.</param>
public sealed record InstanceReply(bool Accepted, Uri? Url = null);

/// <summary>
/// One running app per data directory. The first instance holds <c>backbloom.lock</c> in the data directory open
/// exclusively (the OS releases it when the process dies, so a crash never leaves a stale lock) and listens on a named
/// pipe derived from the directory; later launches with the same directory hand their request over that pipe and exit.
/// Instances with different data directories run side by side (tests, a second configuration). Replaces the C++
/// process name check, which refused a second start with an error.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string LockFileName = "backbloom.lock";

    private readonly FileStream _lock;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;

    private SingleInstance(FileStream lockFile, string pipeName)
    {
        _lock = lockFile;
        PipeName = pipeName;
    }

    public string PipeName { get; }

    /// <summary>Takes the data directory; null when another instance holds it.</summary>
    public static SingleInstance? TryAcquire(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var stream = new FileStream(Path.Combine(dataDirectory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return new SingleInstance(stream, PipeNameFor(dataDirectory));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The pipe of the instance that owns <paramref name="dataDirectory"/>.</summary>
    public static string PipeNameFor(string dataDirectory)
    {
        var normalized = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            normalized = normalized.ToUpperInvariant();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"Backbloom-{Convert.ToHexStringLower(hash.AsSpan(0, 8))}";
    }

    /// <summary>
    /// Sends <paramref name="request"/> to the instance that owns <paramref name="dataDirectory"/>; null when none
    /// answered within <paramref name="timeout"/>.
    /// </summary>
    public static async Task<InstanceReply?> SendAsync(string dataDirectory, InstanceRequest request, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeNameFor(dataDirectory), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cancel.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(request.ToString().AsMemory(), cancel.Token);
            var line = await reader.ReadLineAsync(cancel.Token);
            return ParseReply(line);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Answers requests of later launches with <paramref name="handle"/> (called on a pool thread).</summary>
    public void Listen(Func<InstanceRequest, InstanceReply> handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (_listener is not null)
        {
            throw new InvalidOperationException("Already listening.");
        }

        _listener = Task.Run(() => ListenAsync(handle, _stop.Token));
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // the listener only ends by cancellation
        }

        _stop.Dispose();
        _lock.Dispose();
    }

    internal static string FormatReply(InstanceReply reply) =>
        !reply.Accepted ? "refused" : reply.Url is null ? "ok" : $"ok {reply.Url.AbsoluteUri}";

    internal static InstanceReply? ParseReply(string? line) => line switch
    {
        "ok" => new InstanceReply(true),
        "refused" => new InstanceReply(false),
        not null when line.StartsWith("ok ", StringComparison.Ordinal) && Uri.TryCreate(line[3..], UriKind.Absolute, out var url) => new InstanceReply(true, url),
        _ => null,
    };

    private async Task ListenAsync(Func<InstanceRequest, InstanceReply> handle, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(timeout.Token);
                var reply = Enum.TryParse<InstanceRequest>(line, ignoreCase: false, out var request) && Enum.IsDefined(request)
                    ? handle(request)
                    : new InstanceReply(false);
                await writer.WriteLineAsync(FormatReply(reply).AsMemory(), timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // a client that went away or sent nothing; wait for the next one
            }
        }
    }
}
