using Microsoft.UI.Dispatching;

namespace ReyDesk.Windows;

/// <summary>Posts work to the UI thread without ever throwing into the caller. Callers are often thread-pool timers, socket loops
/// and CancellationToken callbacks, where an exception from the WinRT dispatcher (InvalidCastException, COMException during
/// shutdown or window teardown) or from the posted work itself would be unhandled and terminate the process.</summary>
internal static class UiDispatch
{
    internal static bool PostSafe(this DispatcherQueue? queue, Action work)
    {
        if (queue is null) return false;
        try
        {
            return queue.TryEnqueue(() =>
            {
                try { work(); }
                catch (Exception ex) { Console.WriteLine($"[UI] Posted work failed: {ex}"); }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UI] Dispatcher unavailable, work dropped: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
