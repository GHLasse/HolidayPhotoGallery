using System.Runtime.InteropServices;

namespace PhotoGallery.App.Native;

/// <summary>
/// Puts the app in a Windows job object that kills every process in it when the app's last handle
/// to the job closes, i.e. when the app exits for any reason. Child processes started later
/// (ExifTool, which itself starts Perl) join the job automatically, so none can outlive the app
/// and keep its folder locked.
/// </summary>
internal static class ChildProcessGuard
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    // Kept for the lifetime of the process; closing it is what kills the children.
    private static nint _job;

    public static void Enable()
    {
        if (_job != 0)
            return;

        var job = CreateJobObject(0, null);
        if (job == 0)
            return;

        var info = new JobObjectExtendedLimitInformationData
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformationData>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            // Failure (e.g. a job that forbids nesting) only loses the safety net; the app still works.
            if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size) &&
                AssignProcessToJobObject(job, GetCurrentProcess()))
            {
                _job = job;
                return;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        CloseHandle(job);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationData
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
