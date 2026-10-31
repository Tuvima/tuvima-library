using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MediaEngine.Api.Services.Plugins.Service;

/// <summary>
/// Adds up the memory of a plugin program and every program it started. A plugin started through a launcher (the Windows Store
/// <c>python3</c>, a shell script, a wrapper) keeps the launcher small while the real work runs as a child, so counting only the
/// process the Library started would let that plugin ignore its memory limit.
/// </summary>
internal static class ProcessTreeMemory
{
    /// <summary>Bytes in use by the process and everything below it. A process that ended between checks counts as zero.</summary>
    public static long Measure(Process root)
    {
        root.Refresh();
        var total = Used(root);
        foreach (var pid in Descendants(ReadParents(), root.Id))
        {
            try
            {
                using var child = Process.GetProcessById(pid);
                total += Used(child);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // It ended between the snapshot and the lookup; there is nothing left to count.
            }
        }

        return total;
    }

    /// <summary>Every process id below <paramref name="root"/>, given each process's parent. Safe against loops and reused ids.</summary>
    internal static IReadOnlyList<int> Descendants(IReadOnlyDictionary<int, int> parentOf, int root)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var (pid, parent) in parentOf)
        {
            if (pid == parent)
            {
                continue;
            }

            if (!children.TryGetValue(parent, out var list))
            {
                children[parent] = list = [];
            }

            list.Add(pid);
        }

        var found = new List<int>();
        var seen = new HashSet<int> { root };
        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            if (!children.TryGetValue(queue.Dequeue(), out var below))
            {
                continue;
            }

            foreach (var pid in below.Where(seen.Add))
            {
                found.Add(pid);
                queue.Enqueue(pid);
            }
        }

        return found;
    }

    private static long Used(Process process)
    {
        process.Refresh();
        // Windows hands out memory a program asks for without counting it as in use until it is touched, so the working set
        // would miss a program that reserved a large block; what it has committed is what it has taken.
        return OperatingSystem.IsWindows() ? Math.Max(process.WorkingSet64, process.PrivateMemorySize64) : process.WorkingSet64;
    }

    internal static Dictionary<int, int> ReadParents()
    {
        if (OperatingSystem.IsWindows())
        {
            return ReadParentsWindows();
        }

        return OperatingSystem.IsLinux() ? ReadParentsLinux() : [];
    }

    private static Dictionary<int, int> ReadParentsLinux()
    {
        var parents = new Dictionary<int, int>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            try
            {
                // "pid (name) state ppid ...": the name may hold spaces and brackets, so read from the last closing bracket.
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent))
                {
                    parents[pid] = parent;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // The process ended or is not ours to read; it cannot be one of the plugin's.
            }
        }

        return parents;
    }

    private static Dictionary<int, int> ReadParentsWindows()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == InvalidHandle)
        {
            return parents;
        }

        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
            {
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return parents;
    }

    private const uint SnapshotProcesses = 0x2;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
