using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Flow.Infrastructure;

/// <summary>
/// Gives memory back at quiet moments. Decoded artwork lives outside the .NET heap, so the garbage
/// collector rarely feels pressure to free covers that scrolled away; an occasional full, compacting
/// collection releases them. When Flow is minimized or hidden to the tray the working set is trimmed too.
/// </summary>
public static class MemoryTrim
{
    private static DateTime _last = DateTime.MinValue;
    private static DispatcherOperation? _pending;

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    /// <summary>Schedules a cleanup when the UI is idle (throttled to once every 20 s unless forced).</summary>
    public static void Request(Dispatcher dispatcher, bool trimWorkingSet = false, bool force = false)
    {
        if (!force && (DateTime.UtcNow - _last).TotalSeconds < 20) return;
        if (_pending != null && _pending.Status == DispatcherOperationStatus.Pending) return;
        _pending = dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _last = DateTime.UtcNow;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();   // finalizers release the native bitmap memory
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            if (trimWorkingSet)
            {
                try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1); } catch { }
            }
            DiagLog.Write($"Memory trimmed (managed {GC.GetTotalMemory(false) / 1048576} MB)");
        });
    }
}
