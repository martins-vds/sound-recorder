using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SoundRecorder.Core;

[assembly: InternalsVisibleTo("SoundRecorder.Core.Tests")]

namespace SoundRecorder.Infrastructure.Analysis.Models;

internal sealed record AnalysisModelResources(long AvailableDiskBytes, long AvailableMemoryBytes, bool SupportedPlatform)
{
    internal static AnalysisModelResources Read(string cacheDirectory)
    {
        var supported = OperatingSystem.IsWindows() &&
            RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.X64;
        if (!supported)
            return new(0, 0, false);
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
            throw new AnalysisModelResourceException("Available RAM could not be measured. Check system resources and explicitly retry.");
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(cacheDirectory))!);
        return new(drive.AvailableFreeSpace, (long)status.AvailablePhysical, true);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
