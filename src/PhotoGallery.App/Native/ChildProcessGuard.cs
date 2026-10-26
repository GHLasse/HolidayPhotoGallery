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
    private const uint JobObjectLimitBreakawayOk = 0x800;
    private const uint CreateBreakawayFromJob = 0x01000000;

    // Kept for the lifetime of the process; closing it is what kills the children.
    private static nint _job;

    public static void Enable()
    {
        if (_job != 0)
            return;

        var job = CreateJobObject(0, null);
        if (job == 0)
            return;

        // Failure (e.g. a job that forbids nesting) only loses the safety net; the app still works.
        if (SetLimits(job, JobObjectLimitKillOnJobClose | JobObjectLimitBreakawayOk) && AssignProcessToJobObject(job, GetCurrentProcess()))
        {
            _job = job;
            return;
        }
        CloseHandle(job);
    }

    /// <summary>
    /// Lets the processes in the job outlive the app: the update's hand-over script is started by the app
    /// and must keep running once the app has closed (it was killed with the app, so updates never ran).
    /// Leftover helpers are then stopped by that script instead.
    /// </summary>
    public static void Release()
    {
        if (_job != 0)
            SetLimits(_job, 0);
    }

    /// <summary>
    /// Starts a program outside the job, so it stays open when the app closes: another app the user edits
    /// photos in is theirs, not a helper of this one. Falls back to an ordinary start without the job.
    /// </summary>
    public static void StartDetached(string program, IEnumerable<string> arguments)
    {
        if (_job == 0)
        {
            var plain = new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var argument in arguments)
                plain.ArgumentList.Add(argument);
            System.Diagnostics.Process.Start(plain)?.Dispose();
            return;
        }
        // Paths can't hold quotes, so quoting each one is enough for the program to split them again.
        var commandLine = string.Join(" ", new[] { program }.Concat(arguments).Select(a => "\"" + a + "\""));
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcess(null, new System.Text.StringBuilder(commandLine), 0, 0, false, CreateBreakawayFromJob, 0,
                           Path.GetDirectoryName(program), ref startup, out var created))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(created.hProcess);
        CloseHandle(created.hThread);
    }

    private static bool SetLimits(nint job, uint flags)
    {
        var info = new JobObjectExtendedLimitInformationData
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = flags },
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformationData>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            return SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? applicationName, System.Text.StringBuilder commandLine, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment,
        string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

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
