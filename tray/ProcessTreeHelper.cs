using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AIContextSyncTray;

internal static class ProcessTreeHelper
{
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static int? FindDescendantProcessId(int parentProcessId, string processName)
    {
        var entries = SnapshotProcesses();
        var queue = new Queue<int>();
        var visited = new HashSet<int> { parentProcessId };
        queue.Enqueue(parentProcessId);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var entry in entries.Where(item => item.ParentProcessId == parent))
            {
                if (Path.GetFileNameWithoutExtension(entry.ExecutableName).Equals(
                    processName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return entry.ProcessId;
                }
                if (visited.Add(entry.ProcessId))
                {
                    queue.Enqueue(entry.ProcessId);
                }
            }
        }
        return null;
    }

    private static IReadOnlyList<ProcessEntry> SnapshotProcesses()
    {
        var result = new List<ProcessEntry>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result.Add(new ProcessEntry(
                    unchecked((int)entry.ProcessId),
                    unchecked((int)entry.ParentProcessId),
                    entry.ExecutableFile));
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    private sealed record ProcessEntry(int ProcessId, int ParentProcessId, string ExecutableName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
