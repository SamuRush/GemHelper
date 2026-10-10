using System.Diagnostics;
using Gem.Win32;

namespace Gem.Core;

/// <summary>
/// High-performance memory management and working set trimming service.
/// Uses forced multi-generation garbage collection and Win32 SetProcessWorkingSetSize(-1, -1)
/// to immediately reclaim unused memory and return unmanaged physical pages to the OS.
/// </summary>
public static class MemoryTrimmer
{
    private static readonly object _trimLock = new();
    private static long _lastTrimTicks = 0;

    /// <summary>
    /// Performs forced garbage collection and working set trimming.
    /// </summary>
    /// <param name="stage">Human-readable description of the operation or trigger reason.</param>
    /// <param name="minIntervalSeconds">Minimum seconds between automatic trims to prevent excessive CPU churn.</param>
    public static void TrimMemory(string stage = "", double minIntervalSeconds = 0.0)
    {
        lock (_trimLock)
        {
            long now = Stopwatch.GetTimestamp();
            if (minIntervalSeconds > 0.0 && _lastTrimTicks > 0)
            {
                double elapsedSec = Stopwatch.GetElapsedTime(_lastTrimTicks, now).TotalSeconds;
                if (elapsedSec < minIntervalSeconds)
                {
                    return;
                }
            }

            try
            {
                var proc = Process.GetCurrentProcess();
                proc.Refresh();
                long beforePrivateMb = proc.PrivateMemorySize64 / (1024 * 1024);
                long beforeWorkingSetMb = proc.WorkingSet64 / (1024 * 1024);
                long beforeGcMb = GC.GetTotalMemory(false) / (1024 * 1024);

                // Forced full Gen 2 GC collection with LOH compaction
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);

                // P/Invoke SetProcessWorkingSetSize(-1, -1) to flush working set
                try
                {
                    NativeMethods.SetProcessWorkingSetSize(proc.Handle, (IntPtr)(-1), (IntPtr)(-1));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MemoryTrimmer] SetProcessWorkingSetSize exception: {ex.Message}");
                }

                proc.Refresh();
                long afterPrivateMb = proc.PrivateMemorySize64 / (1024 * 1024);
                long afterWorkingSetMb = proc.WorkingSet64 / (1024 * 1024);
                long afterGcMb = GC.GetTotalMemory(false) / (1024 * 1024);

                _lastTrimTicks = Stopwatch.GetTimestamp();

                Console.ForegroundColor = ConsoleColor.Green;
                string label = string.IsNullOrWhiteSpace(stage) ? "" : $" [{stage}]";
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss.fff}] [MemoryTrimmer]{label} Сброс памяти: " +
                    $"Working Set: {beforeWorkingSetMb} МБ -> {afterWorkingSetMb} МБ | " +
                    $"Private Memory: {beforePrivateMb} МБ -> {afterPrivateMb} МБ | " +
                    $"GC Heap: {beforeGcMb} МБ -> {afterGcMb} МБ");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MemoryTrimmer Warning] {ex.Message}");
            }
        }
    }
}
