using System.Collections.Concurrent;

namespace DeltaNFD.Inspection;
/// <summary>One background reader. Opening a page cannot launch a competing full inspection.</summary>
public static class InspectionScheduler
{
    private static readonly BlockingCollection<Action> Queue = new();
    [ThreadStatic]
    private static bool _reader;
    static InspectionScheduler()
    {
        var worker = new Thread(() =>
        {
            _reader = true;
            foreach (var action in Queue.GetConsumingEnumerable())
                action();
        })
        {
            IsBackground = true,
            Name = "DeltaNFD.ReadOnlyInspection",
            Priority = ThreadPriority.BelowNormal
        };
        worker.Start();
    }

    public static Task<T> Run<T>(Func<T> read)
    {
        if (_reader)
        {
            try
            {
                return Task.FromResult(read());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            try
            {
                result.SetResult(read());
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        });
        return result.Task;
    }

    public static Task<T> Run<T>(Func<Task<T>> read) => Run(() => read().GetAwaiter().GetResult());
}
