using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Backbloom.Engine.Platform;

namespace Backbloom.Platform.Windows;

/// <summary>
/// The programs with a window the user can see: the top-level windows (<c>EnumWindows</c>) that are visible, have a
/// title, no owner, aren't tool windows and aren't cloaked (hidden Store apps and windows on other virtual desktops
/// report themselves visible), with the executable name of their process
/// (<c>QueryFullProcessImageNameW</c>, opened with limited query rights only). The first window of a program in
/// z-order (the most recently used) gives its title.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class WindowsProgramList : IProgramList
{
    private const int GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const uint DwmwaCloaked = 14;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaxTitle = 512;
    private const int MaxPath = 32_768;

    public IReadOnlyList<RunningProgram> WindowedPrograms()
    {
        var windows = new List<nint>();
        var handle = GCHandle.Alloc(windows);
        try
        {
            if (EnumWindows(&Collect, GCHandle.ToIntPtr(handle)) == 0)
            {
                throw new InvalidOperationException($"EnumWindows failed (error {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            handle.Free();
        }

        var own = (uint)Environment.ProcessId;
        var names = new Dictionary<uint, string?>();
        var programs = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in windows)
        {
            if (!IsWindowVisible(window) || GetWindow(window, GwOwner) != 0 || (GetWindowLongW(window, GwlExStyle) & WsExToolWindow) != 0 ||
                IsCloaked(window) || Title(window) is not { } title)
            {
                continue;
            }

            uint pid;
            _ = GetWindowThreadProcessId(window, &pid);
            if (pid == own || pid == 0)
            {
                continue;
            }

            if (!names.TryGetValue(pid, out var executable))
            {
                executable = ExecutableName(pid);
                names[pid] = executable;
            }

            if (executable is not null)
            {
                programs.TryAdd(executable, new RunningProgram(executable, title));
            }
        }

        return [.. programs.Values.OrderBy(p => p.Executable, StringComparer.OrdinalIgnoreCase)];
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint window, nint state)
    {
        ((List<nint>)GCHandle.FromIntPtr(state).Target!).Add(window);
        return 1;
    }

    private static bool IsCloaked(nint window)
    {
        int cloaked;
        return DwmGetWindowAttribute(window, DwmwaCloaked, &cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static string? Title(nint window)
    {
        var buffer = stackalloc char[MaxTitle];
        var length = GetWindowTextW(window, buffer, MaxTitle);
        var title = length > 0 ? new string(buffer, 0, length).Trim() : string.Empty;
        return title.Length > 0 ? title : null;
    }

    private static string? ExecutableName(uint pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, 0, pid);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var buffer = new char[MaxPath];
            var length = (uint)buffer.Length;
            fixed (char* path = buffer)
            {
                return QueryFullProcessImageNameW(process, 0, path, &length) != 0
                    ? Path.GetFileName(new string(path, 0, (int)length))
                    : null;
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowLongW(nint window, int index);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextW(nint window, char* text, int maxCount);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, uint* processId);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, uint attribute, void* value, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, int inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial int QueryFullProcessImageNameW(nint process, uint flags, char* name, uint* size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
