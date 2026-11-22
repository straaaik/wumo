using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PixelThumbs;

internal static class RenderJob
{
    internal const int ProcessMemoryLimit = 768 * 1024 * 1024;
    internal const long JobMemoryLimit = 1024L * 1024 * 1024;
    private const uint LimitFlags = 0x2000 | 0x100 | 0x200;

    internal static SafeFileHandle? Attach(Process process)
    {
        var job = CreateJobObject(0, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            return null;
        }

        var limits = new ExtendedLimitInformation
        {
            Basic = new BasicLimitInformation { Flags = LimitFlags },
            ProcessMemoryLimit = ProcessMemoryLimit,
            JobMemoryLimit = (nuint)JobMemoryLimit
        };
        if (SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>())
            && AssignProcessToJobObject(job, process.SafeHandle))
            return job;

        job.Dispose();
        return null;
    }

    internal static (long Process, long Job) PeakMemory(SafeFileHandle job) =>
        QueryInformationJobObject(job, 9, out var info, (uint)Marshal.SizeOf<ExtendedLimitInformation>(), 0)
            ? ((long)info.PeakProcessMemoryUsed, (long)info.PeakJobMemoryUsed) : default;

    internal static bool IsMemoryLimited() =>
        QueryCurrentJob(0, 9, out var info, (uint)Marshal.SizeOf<ExtendedLimitInformation>(), 0) &&
        (info.Basic.Flags & LimitFlags) == LimitFlags && info.ProcessMemoryLimit <= ProcessMemoryLimit &&
        info.JobMemoryLimit <= (nuint)JobMemoryLimit;

    internal static void Terminate(SafeFileHandle job) => TerminateJobObject(job, 1);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, BytesRead, BytesWritten, OtherBytes;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind,
        ref ExtendedLimitInformation information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind,
        out ExtendedLimitInformation information, uint size, nint returnedSize);

    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryCurrentJob(nint job, int kind, out ExtendedLimitInformation information,
        uint size, nint returnedSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
