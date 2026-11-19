using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlyphLens.Windows.Worker;

/// <summary>
/// A Windows Job Object configured to kill every process in it when its last handle closes.
/// </summary>
/// <remarks>
/// <para>
/// The application keeps the only handle, so when GlyphLens exits for any reason (including being killed) Windows
/// terminates the worker, and anything the worker started, which makes an orphaned worker impossible by construction.
/// Windows 8 and later support nested jobs, so this works even when the app itself runs inside another job.
/// </para>
/// <para>
/// Limitation: <see cref="Process.Start()"/> cannot create a process suspended, so there is a brief window between
/// process creation and assignment in which a child that spawns grandchildren immediately would leave them outside the
/// job (this matters for virtual-environment interpreter launchers). The worker therefore also exits on its own when
/// the pipe closes, which is the second line of defence.
/// </para>
/// </remarks>
internal sealed unsafe partial class WorkerJobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private nint _handle;

    public WorkerJobObject()
    {
        _handle = CreateJobObjectW(0, 0);
        if (_handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the worker job object.");
        }

        var info = default(ExtendedLimitInformation);
        info.Basic.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformationClass, &info, (uint)sizeof(ExtendedLimitInformation)))
        {
            var error = Marshal.GetLastPInvokeError();
            Dispose();
            throw new Win32Exception(error, "Could not configure the worker job object.");
        }
    }

    /// <summary>Adds <paramref name="process"/> to the job. After this the OS ends it when the job is closed.</summary>
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(_handle == 0, this);

        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not assign the worker to the job object.");
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            _ = CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateJobObjectW(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int informationClass, void* information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
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
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
