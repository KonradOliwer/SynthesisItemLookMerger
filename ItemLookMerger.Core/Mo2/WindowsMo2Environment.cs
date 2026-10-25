using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ItemLookMerger.Core.Mo2;

/// <summary>
/// The real machine: walks the process ancestry with a Toolhelp snapshot and reads MO2's registry key. A parent counts only when it
/// started before its child, so a reused process id cannot lead the walk into an unrelated process.
/// </summary>
public sealed class WindowsMo2Environment : IMo2Environment
{
    private const string ExecutableName = "ModOrganizer.exe";
    private const string RegistryKey = @"Software\Mod Organizer Team\Mod Organizer";
    private const string CurrentInstanceValue = "CurrentInstance";

    public string? LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } path ? path : null;

    public Mo2ProcessLookup FindModOrganizer()
    {
        if (!OperatingSystem.IsWindows()) return new Mo2ProcessLookup(null);
        var parents = ProcessParents();
        var visited = new HashSet<int>();
        var pid = Environment.ProcessId;
        var childStart = StartTime(pid);
        while (parents.TryGetValue(pid, out var entry) && visited.Add(pid))
        {
            var parentId = entry.ParentId;
            if (!parents.TryGetValue(parentId, out var parent)) break;
            var parentStart = StartTime(parentId);
            // A parent that started after its child is a newer process that reused the id: the real parent is gone.
            if (parentStart == null || childStart == null || parentStart > childStart) break;
            if (string.Equals(parent.ExeName, ExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                var path = ImagePath(parentId, out var error);
                return path != null
                    ? new Mo2ProcessLookup(path)
                    : new Mo2ProcessLookup(null, $"ModOrganizer.exe (process {parentId}) started this patcher, but its path could not be read: {error}.");
            }
            pid = parentId;
            childStart = parentStart;
        }
        return new Mo2ProcessLookup(null);
    }

    public string? CurrentGlobalInstance()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
        return key?.GetValue(CurrentInstanceValue) as string;
    }

    /// <summary>The process's start time (as a FILETIME number), or null when it cannot be read.</summary>
    private static long? StartTime(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            return GetProcessTimes(handle, out var creation, out _, out _, out _) ? creation : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>The executable path via QueryFullProcessImageName, which works across elevation levels with limited query rights.</summary>
    private static string? ImagePath(int pid, out string error)
    {
        error = string.Empty;
        var handle = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            error = $"OpenProcess failed with Windows error {Marshal.GetLastWin32Error()}";
            return null;
        }
        try
        {
            var buffer = new StringBuilder(32768);
            var size = buffer.Capacity;
            if (QueryFullProcessImageNameW(handle, 0, buffer, ref size)) return buffer.ToString(0, size);
            error = $"QueryFullProcessImageName failed with Windows error {Marshal.GetLastWin32Error()}";
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Every running process: its parent id and executable name.</summary>
    private static Dictionary<int, (int ParentId, string ExeName)> ProcessParents()
    {
        var result = new Dictionary<int, (int, string)>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle) return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                result[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExeFile);
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    private const uint SnapProcess = 0x00000002;
    private const uint QueryLimitedInformation = 0x1000;
    private static readonly IntPtr InvalidHandle = new(-1);

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
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
