using System.Runtime.InteropServices;

namespace ScanSorter.Config;

/// <summary>Bounds concurrency by CPU and available memory; never probes document contents.</summary>
public static class WorkerPolicy
{
    private const long MiB = 1024 * 1024;
    public static int Resolve(int requested, int? processorCount = null, long? availableBytes = null)
    {
        int cpus = processorCount ?? Environment.ProcessorCount;
        int preferred = requested > 0 ? Math.Clamp(requested, 1, 8) : Math.Clamp(cpus - 1, 1, 4);
        long? available = availableBytes ?? AvailablePhysicalMemory();
        if (!available.HasValue) return preferred;
        // Leave room for the UI, PDFium and the shared model; each OCR worker owns its page and crops.
        int memoryLimit = (int)Math.Clamp((available.Value - 384 * MiB) / (256 * MiB), 1, 8);
        return Math.Min(preferred, memoryLimit);
    }

    private static long? AvailablePhysicalMemory()
    {
        try
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            return GlobalMemoryStatusEx(ref status) ? (long)Math.Min(status.AvailablePhysical, (ulong)long.MaxValue) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
