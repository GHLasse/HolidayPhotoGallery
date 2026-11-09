using System.Runtime.InteropServices;

namespace PhotoGallery.App.Native;

/// <summary>
/// How full the PC's memory is right now, read from Windows each time (the .NET figure only changes when the app
/// itself collects garbage, so it could say "full" long after another app has let go of its memory).
/// </summary>
internal static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>Percent of the physical memory in use (0-100), or -1 when Windows doesn't say.</summary>
    public static int LoadPercent()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? (int)status.MemoryLoad : -1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }
    }

    /// <summary>The memory is nearly full, so work that can wait (background jobs, thumbnails off screen) waits.</summary>
    public static bool IsLow => Core.MemoryTrouble.IsLow(LoadPercent());

    private static long _lastTrim;

    /// <summary>
    /// After a lack of memory: lets the garbage collector hand back what the app no longer uses, at most every 30 s
    /// (a full collection is slow, and doing it for every failed thumbnail would add to the trouble).
    /// </summary>
    public static void TrimSoon()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastTrim);
        if (now - last < 30_000 || Interlocked.CompareExchange(ref _lastTrim, now, last) != last)
            return;
        App.Trace($"Memory short (load {LoadPercent()} %); freeing what the app no longer needs.");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            catch (Exception)
            {
                // Only a help; nothing depends on it.
            }
        });
    }
}
