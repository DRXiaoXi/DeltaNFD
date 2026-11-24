using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DeltaNFD.Native;

internal static class FrameDisplayChecks
{
    private static readonly FrameDisplayTarget Monitor = new(1, 0, 1, "monitor");
    private static readonly FakeMode Original = new(Monitor, 2560, 1440, 2560, 1440, false);

    internal static void ReadNative()
    {
        var display = new FrameDisplayMode();
        if (display.Read() is not { } current)
        {
            Console.WriteLine("No adjustable primary display was accessible from this desktop session.");
            return;
        }
        Console.WriteLine($"CCD current desktop: {current.Width} x {current.Height}.");
        var resolutions = new (uint Width, uint Height)[] { (1440, 1080), (1280, 960), (1600, 1080), (1280, 1024) };
        foreach (var (width, height) in resolutions)
        {
            var valid = display.FindStretch(current, width, height) is not null;
            Console.WriteLine($"CCD validate-only {width} x {height}, stretched: {valid}.");
        }
        if (display.Read(current.Target) is not { } after || !current.SameMode(after))
            throw new InvalidOperationException("Display configuration differed after read-only validation.");
    }

    internal static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks++;
        }

        int NativeSize(string name) => Marshal.SizeOf(typeof(FrameDisplayMode).GetNestedType(name, BindingFlags.NonPublic)!);
        Check(NativeSize("PathInfo") == 72 && NativeSize("ModeInfo") == 64 && NativeSize("TargetName") == 420,
            "CCD structures must match Windows SDK layout");

        var now = Stopwatch.Frequency;
        var display = new FakeDisplay();
        var session = new FrameDisplaySession(display, () => now);
        var invalid = session.Handle("apply 1440 -1080");
        Check(!invalid.Success && display.Writes == 0, "invalid pixels must not be written");
        Check(!session.Handle("apply 2560 1440").Success && display.Writes == 0, "identity scaling must be rejected");
        display.Reject = true;
        Check(!session.Handle("apply 1440 1080").Success && display.Writes == 0, "failed validation must not switch mode");
        display.Reject = false;

        display.CenterInstead = true;
        var fallback = session.Handle("apply 1440 1080");
        Check(!fallback.Success && fallback.State == FrameDisplayState.Idle && display.Current == Original && display.Writes == 2,
            "correct source pixels with aspect-preserving scaling must roll back");
        display.CenterInstead = false;
        display.ChangeSignal = true;
        fallback = session.Handle("apply 1440 1080");
        Check(!fallback.Success && fallback.State == FrameDisplayState.Idle && display.Current == Original,
            "correct source pixels with a different output signal must roll back");
        display.ChangeSignal = false;

        var applied = session.Handle("apply 1440 1080");
        Check(applied is { Success: true, State: FrameDisplayState.Preview }
            && applied.Deadline == now + 15 * Stopwatch.Frequency && display.Current is { Width: 1440, Stretched: true },
            "user sees a 15-second preview only after verifying the GPU stretch");
        Check(!session.Handle("apply 1280 960").Success, "pending preview must reject concurrent apply");
        now = applied.Deadline - 1;
        Check(session.Handle($"keep {applied.Deadline}").State == FrameDisplayState.Kept, "confirmation before deadline keeps the mode");
        now++;
        var second = session.Handle("apply 1280 960");
        Check(!session.Handle($"cancel {applied.Deadline}").Success && display.Current.Width == 1280,
            "an old confirmation cannot cancel a new preview");
        now = second.Deadline;
        Check(!session.Handle($"keep {second.Deadline}").Success && display.Current == Original,
            "confirmation on the deadline must lose to rollback and restore the pre-Frame mode");

        var changedBeforeConfirmation = session.Handle("apply 1440 1080");
        display.Current = display.Current with { Stretched = false };
        Check(!session.Handle($"keep {changedBeforeConfirmation.Deadline}").Success && display.Current == Original,
            "changes between preview and confirmation must be revalidated");
        var cancelled = session.Handle("apply 1440 1080");
        Check(session.Handle($"cancel {cancelled.Deadline}").Success && display.Current == Original,
            "explicit rejection restores the original mode");

        var keep = session.Handle("apply 1440 1080");
        session.Handle($"keep {keep.Deadline}");
        display.Accessible = false;
        var disconnected = session.Handle("restore");
        Check(!disconnected.Success && disconnected.State == FrameDisplayState.RestorePending
            && display.Current.Width == 1440, "unplugging the monitor preserves the original for retry");
        Check(!session.Handle("apply 1280 960").Success, "pending restore prevents another display override");
        display.Accessible = true;
        Check(session.Handle("restore").Success && display.Current == Original, "normal frame close restores after reconnect");

        // No UI or incoming command is needed for the real timer to fire.
        var timerDisplay = new FakeDisplay();
        var input = new CommandReader();
        var output = new ReplyWriter();
        var running = new FrameDisplaySession(timerDisplay).RunAsync(input, output);
        try
        {
            input.Send("apply 1440 1080");
            var preview = await output.ReceiveAsync();
            var restoredAt = await timerDisplay.Restored.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Check(restoredAt >= preview.Deadline && restoredAt - preview.Deadline < 5 * Stopwatch.Frequency,
                "the unattended worker restores at the deadline");

            input.Send("apply 1440 1080");
            var kept = await output.ReceiveAsync();
            input.Send($"keep {kept.Deadline}");
            Check((await output.ReceiveAsync()).State == FrameDisplayState.Kept, "worker accepts a valid confirmation");
        }
        finally { input.CloseInput(); }
        Check(await running.WaitAsync(TimeSpan.FromSeconds(5)) == 0 && timerDisplay.Current == Original,
            "closing the UI process input restores a confirmed change before the worker exits");
        Console.WriteLine($"Frame display checks passed: {checks}.");
    }

    private sealed record FakeMode(FrameDisplayTarget Target, uint Width, uint Height, uint OutputWidth,
        uint OutputHeight, bool Stretched) : IFrameDisplayMode
    {
        public bool SameMode(IFrameDisplayMode other) => Equals(other);
        public bool MatchesStretch(IFrameDisplayMode actual) => Stretched && Equals(actual);
    }

    private sealed class FakeDisplay : IFrameDisplay
    {
        internal bool Accessible = true, Reject, CenterInstead, ChangeSignal;
        internal int Writes { get; private set; }
        internal FakeMode Current { get; set; } = Original;
        internal TaskCompletionSource<long> Restored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IFrameDisplayMode? Read(FrameDisplayTarget? target = null) =>
            Accessible && (target is null || target == Current.Target) ? Current : null;
        public IFrameDisplayMode? FindStretch(IFrameDisplayMode current, uint width, uint height) =>
            Reject || width == Original.OutputWidth && height == Original.OutputHeight
                ? null : new FakeMode(current.Target, width, height, Original.OutputWidth, Original.OutputHeight, true);

        public int Change(IFrameDisplayMode mode, IFrameDisplayMode expected)
        {
            if (!Current.SameMode(expected)) return 1610;
            var next = (FakeMode)mode;
            Writes++;
            Current = next.Stretched
                ? next with { Stretched = !CenterInstead, OutputWidth = ChangeSignal ? next.Width : next.OutputWidth }
                : next;
            if (Current == Original) Restored.TrySetResult(Stopwatch.GetTimestamp());
            return 0;
        }
    }

    private sealed class CommandReader : TextReader
    {
        private readonly Channel<string> _commands = Channel.CreateUnbounded<string>();
        internal void Send(string command) => _commands.Writer.TryWrite(command);
        internal void CloseInput() => _commands.Writer.TryComplete();
        public override string? ReadLine() => _commands.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()
            && _commands.Reader.TryRead(out var next) ? next : null;
    }

    private sealed class ReplyWriter : TextWriter
    {
        private readonly Channel<string> _replies = Channel.CreateUnbounded<string>();
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(string? value)
        {
            if (value is not null) _replies.Writer.TryWrite(value);
            return Task.CompletedTask;
        }
        internal async Task<FrameDisplayReply> ReceiveAsync() =>
            JsonSerializer.Deserialize<FrameDisplayReply>(await _replies.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))!;
    }
}
