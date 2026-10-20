using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Overglow.Engine.Platform;

namespace Overglow.Platform.Windows;

/// <summary>
/// Looks for running programs with one Toolhelp process snapshot (<c>CreateToolhelp32Snapshot</c> +
/// <c>Process32NextW</c>): image names only, no process handles opened, about a millisecond per call, nothing
/// allocated per process (unlike <c>Process.GetProcesses</c>, which builds an object per process).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class WindowsProcessList : IProcessList
{
    private const uint SnapProcess = 0x2;
    private const int MaxPath = 260;

    public string? FindRunning(IReadOnlyList<string> executableNames)
    {
        ArgumentNullException.ThrowIfNull(executableNames);
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == -1)
        {
            throw new InvalidOperationException($"CreateToolhelp32Snapshot failed (error {Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            var entry = new ProcessEntry { Size = (uint)sizeof(ProcessEntry) };
            for (var more = Process32FirstW(snapshot, &entry); more; more = Process32NextW(snapshot, &entry))
            {
                var name = new ReadOnlySpan<char>(entry.ExeFile, MaxPath);
                var end = name.IndexOf('\0');
                name = end < 0 ? name : name[..end];
                foreach (var wanted in executableNames)
                {
                    if (name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return wanted;
                    }
                }
            }

            return null;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[MaxPath];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint snapshot, ProcessEntry* entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint snapshot, ProcessEntry* entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
