using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Relay.Core.Agents;

namespace Relay.Windows;

/// <summary>
/// Confines each worker in its own Windows job object: kill-on-close (the worker cannot outlive
/// Relay or its run), a hard process-memory cap, an active-process limit of one (no children),
/// die-on-unhandled-exception, and the full UI restriction set (no clipboard, desktop, display,
/// global atoms, or handles from other processes). Combined with the cleared environment and
/// staging working directory from <see cref="ProcessWorkerHost"/>, the worker's only channel to
/// anything is the broker pipe.
/// </summary>
public sealed class JobObjectWorkerHost : ProcessWorkerHost
{
    private readonly List<SafeJobHandle> _jobs = new();

    public JobObjectWorkerHost(string workerPath) : base(workerPath) { }

    public override string Description => "job object sandbox (kill-on-close, 1 process, memory cap, UI restrictions)";

    protected override void OnStarted(Process process, AgentRunSpec spec)
    {
        var job = new SafeJobHandle(Job.CreateJobObjectW(IntPtr.Zero, null));
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");

        var limits = new Job.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new Job.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = Job.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | Job.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION
                           | Job.JOB_OBJECT_LIMIT_ACTIVE_PROCESS | Job.JOB_OBJECT_LIMIT_PROCESS_MEMORY,
                ActiveProcessLimit = 1,
            },
            ProcessMemoryLimit = (UIntPtr)Math.Max(64L * 1024 * 1024, spec.Limits.MaxMemoryBytes),
        };
        var size = Marshal.SizeOf<Job.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!Job.SetInformationJobObject(job, Job.JobObjectExtendedLimitInformation, buffer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject(limits) failed.");
        }
        finally { Marshal.FreeHGlobal(buffer); }

        var ui = new Job.JOBOBJECT_BASIC_UI_RESTRICTIONS { UIRestrictionsClass = Job.JOB_OBJECT_UILIMIT_ALL };
        var uiSize = Marshal.SizeOf<Job.JOBOBJECT_BASIC_UI_RESTRICTIONS>();
        var uiBuffer = Marshal.AllocHGlobal(uiSize);
        try
        {
            Marshal.StructureToPtr(ui, uiBuffer, false);
            if (!Job.SetInformationJobObject(job, Job.JobObjectBasicUIRestrictions, uiBuffer, (uint)uiSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject(ui) failed.");
        }
        finally { Marshal.FreeHGlobal(uiBuffer); }

        if (!Job.AssignProcessToJobObject(job, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");

        lock (_jobs)
        {
            _jobs.Add(job);
            // Release handles of jobs whose only process has exited; closing the last handle kills anything left.
            _jobs.RemoveAll(j => { if (j.Owner is { HasExited: true }) { j.Dispose(); return true; } return false; });
        }
        job.Owner = process;
    }

    private sealed class SafeJobHandle : SafeHandle
    {
        public SafeJobHandle(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(handle);
        public Process? Owner { get; set; }
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => Job.CloseHandle(handle);
    }

    private static class Job
    {
        public const int JobObjectExtendedLimitInformation = 9;
        public const int JobObjectBasicUIRestrictions = 4;

        public const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
        public const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
        public const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        public const uint JOB_OBJECT_UILIMIT_ALL = 0x000000FF; // HANDLES | READCLIPBOARD | WRITECLIPBOARD | SYSTEMPARAMETERS | DISPLAYSETTINGS | GLOBALATOMS | DESKTOP | EXITWINDOWS

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_UI_RESTRICTIONS
        {
            public uint UIRestrictionsClass;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern IntPtr CreateJobObjectW(IntPtr securityAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(SafeHandle job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(SafeHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
