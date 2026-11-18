// The actual update view model is linked into the smoke executable. Only its WinUI boundary is replaced.
namespace Microsoft.UI.Xaml.Controls
{
    public enum InfoBarSeverity { Informational, Success, Warning, Error }
}

namespace Microsoft.UI.Dispatching
{
    public delegate void DispatcherQueueHandler();

    public sealed class DispatcherQueue
    {
        internal static DispatcherQueue? CurrentForTests { get; set; }
        private readonly System.Collections.Concurrent.ConcurrentQueue<DispatcherQueueHandler> _pending = new();
        public static DispatcherQueue? GetForCurrentThread() => CurrentForTests;
        public bool TryEnqueue(DispatcherQueueHandler action) { _pending.Enqueue(action); return true; }
        internal void Drain() { while (_pending.TryDequeue(out var action)) action(); }
    }
}
