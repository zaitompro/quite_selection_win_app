using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QuietSelection.App.Workers;

/// <summary>Closing the parent process also terminates its in-flight helper.</summary>
internal sealed class WorkerJob : IDisposable
{
    private readonly SafeFileHandle handle;
    internal WorkerJob(nint processHandle, ulong memoryLimit = 0)
    {
        handle = CreateJobObject(0, null);
        if (handle.IsInvalid) throw new QuietException(ErrorCode.WorkerFailure);
        var info = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000u | (memoryLimit > 0 ? 0x100u : 0u) }, ProcessMemoryLimit = (nuint)memoryLimit };
        if (!SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()) ||
            !AssignProcessToJobObject(handle, processHandle))
        { handle.Dispose(); throw new QuietException(ErrorCode.WorkerFailure); }
    }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        internal BasicLimits Basic; internal IoCounters Io;
        internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits information, uint size);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
}
