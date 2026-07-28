using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Supprocom.NativeAllocationManagement.Demos.VoxelChunkPipeline.Harness;

internal sealed class WindowsProcessLifetimeJob : IDisposable
{
    private const uint KillOnJobClose = 0x00002000;
    private const int ExtendedLimitInformationClass = 9;
    private SafeFileHandle? _handle;

    internal WindowsProcessLifetimeJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SafeFileHandle handle = CreateJobObjectW(
            IntPtr.Zero,
            null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not create the child-process job.");
        }

        JobObjectExtendedLimitInformation information = new()
        {
            BasicLimitInformation =
                new JobObjectBasicLimitInformation
                {
                    LimitFlags = KillOnJobClose
                }
        };
        if (!SetInformationJobObject(
                handle,
                ExtendedLimitInformationClass,
                ref information,
                Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                "Windows could not configure the child-process job.");
        }

        _handle = handle;
    }

    internal void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        SafeFileHandle? handle = _handle;
        if (handle is null)
        {
            return;
        }

        if (!AssignProcessToJobObject(
                handle,
                process.Handle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not assign a child process to its lifetime job.");
        }
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation
            BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateJobObjectW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(
        IntPtr jobAttributes,
        string? name);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        int informationLength);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        IntPtr process);
}
