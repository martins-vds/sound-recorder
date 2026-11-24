using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SoundRecorder.Core;

[assembly: InternalsVisibleTo("SoundRecorder.Core.Tests")]

namespace SoundRecorder.Infrastructure.Analysis.Models;

internal sealed record AnalysisModelResources(long AvailableDiskBytes, long AvailableMemoryBytes, bool SupportedPlatform)
{
    internal static AnalysisModelResources Read(string cacheDirectory)
        => Read(OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture,
            ReadMemory, () => new DriveInfo(Path.GetPathRoot(cacheDirectory)!).AvailableFreeSpace);

    internal static AnalysisModelResources Read(bool windows, Architecture architecture,
        Func<(bool Measured, long AvailableBytes)> memory, Func<long> disk)
    {
        var supported = windows && architecture is Architecture.Arm64 or Architecture.X64;
        if (!supported)
            return new(0, 0, false);
        var available = memory();
        if (!available.Measured)
            throw new AnalysisModelResourceException("Available RAM could not be measured. Check system resources and explicitly retry.");
        return new(disk(), available.AvailableBytes, true);
    }

    private static (bool Measured, long AvailableBytes) ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var measured = GlobalMemoryStatusEx(ref status);
        return (measured, (long)status.AvailablePhysical);
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
