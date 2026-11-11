using System.Globalization;
using System.Runtime.InteropServices;
using Magic.Files;

namespace Magic.SdlPlatform;

/// <summary>
/// The memory this process alone holds in RAM, as the OS's own tools show it: Windows' private working set (Task
/// Manager's Memory column), Linux's anonymous resident pages (RssAnon), macOS's physical footprint (Activity Monitor's
/// Memory). Shared libraries' pages and memory paged out are not in it. .NET and SDL have no such number, so it is asked
/// of the OS here.
/// </summary>
internal static unsafe partial class ProcessMemory
{
    /// <summary>
    /// <c>GetCurrentProcess</c>'s pseudo handle, which needs no closing.
    /// </summary>
    private const nint CurrentProcess = -1;

    /// <summary>
    /// <c>RUSAGE_INFO_V2</c>, and where its <c>ri_phys_footprint</c> is in it, counted in 64-bit words: a 16-byte uuid,
    /// then user time, system time, idle wakeups, interrupt wakeups, page-ins, wired and resident size.
    /// </summary>
    private const int RusageInfoV2 = 2;

    private const int RusageInfoV2Words = 20;

    private const int PhysicalFootprintWord = 9;

    private const string LinuxStatus = "/proc/self/status";

    private const string LinuxAnonymousResident = "RssAnon:";

    /// <summary>
    /// Bytes of RAM only this process holds now; null where the OS doesn't say.
    /// </summary>
    public static long? GetPrivateResidentBytes(IFileSystem files)
    {
        if (OperatingSystem.IsWindows())
        {
            ProcessMemoryCountersEx2 counters = new() { Size = (uint)sizeof(ProcessMemoryCountersEx2) };

            return K32GetProcessMemoryInfo(CurrentProcess, &counters, counters.Size)
                ? (long)counters.PrivateWorkingSetSize
                : null;
        }

        if (OperatingSystem.IsMacOS())
        {
            ulong* info = stackalloc ulong[RusageInfoV2Words];

            return proc_pid_rusage(Environment.ProcessId, RusageInfoV2, info) == 0
                ? (long)info[PhysicalFootprintWord]
                : null;
        }

        if (OperatingSystem.IsLinux())
            return ReadLinuxAnonymousResident(files);

        return null;
    }

    /// <summary>
    /// RssAnon in <c>/proc/self/status</c>, a line such as <c>RssAnon:	  123456 kB</c>.
    /// </summary>
    private static long? ReadLinuxAnonymousResident(IFileSystem files)
    {
        if (files.ReadLines(LinuxStatus) is not { Ok: true, Value: { } lines })
            return null;

        foreach (string line in lines)
        {
            if (!line.StartsWith(LinuxAnonymousResident, StringComparison.Ordinal))
                continue;

            ReadOnlySpan<char> kilobytes = line.AsSpan(LinuxAnonymousResident.Length).Trim().TrimEnd("kB").Trim();

            return long.TryParse(kilobytes, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
                ? value * 1024
                : null;
        }

        return null;
    }

    [LibraryImport("kernel32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool K32GetProcessMemoryInfo(nint process, ProcessMemoryCountersEx2* counters, uint size);

    [LibraryImport("libproc")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int proc_pid_rusage(int pid, int flavor, ulong* buffer);

    /// <summary>
    /// <c>PROCESS_MEMORY_COUNTERS_EX2</c>, Windows 10 1809 and later.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }
}
