using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace DeltaNFD.Native;

internal enum FrameDisplayState { Idle, Preview, Kept, RestorePending }

internal sealed record FrameDisplayReply(bool Success, FrameDisplayState State, string Message, long Deadline = 0);

// Runs in a separate process so minimizing, closing the dialog, or losing the UI process
// cannot interrupt the countdown. The redirected input closes when the UI process exits.
internal sealed class FrameDisplaySession(IFrameDisplay display, Func<long>? timestamp = null)
{
    internal const string Argument = "--frame-display-session";
    internal const int ConfirmationSeconds = 15;

    private readonly Func<long> _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    private IFrameDisplayMode? _original;
    private IFrameDisplayMode? _preview;
    private FrameDisplayState _state;
    private long _deadline;
    private string _status = "当前没有需要恢复的临时分辨率。";

    internal async Task<int> RunAsync(TextReader input, TextWriter output)
    {
        int exitCode;
        try
        {
            // Console.In can execute ReadLineAsync synchronously. Keep its blocking read off the timer path.
            var read = Task.Run(input.ReadLine);
            while (true)
            {
                if (_deadline != 0)
                {
                    var remaining = Stopwatch.GetElapsedTime(_timestamp(), _deadline);
                    if (remaining <= TimeSpan.Zero || await Task.WhenAny(read, Task.Delay(remaining)) != read)
                    {
                        ExpirePreview();
                        continue;
                    }
                }
                var line = await read;
                if (line is null) break;
                await output.WriteLineAsync(JsonSerializer.Serialize(Handle(line)));
                await output.FlushAsync();
                read = Task.Run(input.ReadLine);
            }
        }
        finally
        {
            exitCode = Restore("帧格已退出。").Success ? 0 : 1;
        }
        return exitCode;
    }

    internal FrameDisplayReply Handle(string command)
    {
        ExpirePreview();
        var parts = command.Split(' ');
        if (parts is ["apply", var widthText, var heightText]
            && uint.TryParse(widthText, NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            && uint.TryParse(heightText, NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            && width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue)
            return Apply(width, height);

        if (parts is ["keep" or "cancel", var deadlineText]
            && long.TryParse(deadlineText, NumberStyles.None, CultureInfo.InvariantCulture, out var deadline))
        {
            if (_state != FrameDisplayState.Preview || deadline != _deadline)
                return Result(parts[0] == "cancel" && _state == FrameDisplayState.Idle, _status);
            if (parts[0] == "cancel") return Restore("已取消。");
            if (_preview is not { } preview || display.Read(preview.Target) is not { } actual || !preview.MatchesStretch(actual))
                return Restore("GPU 拉伸状态发生变化。") with { Success = false };
            if (_timestamp() >= _deadline) return Restore("15 秒内未确认。") with { Success = false };
            _preview = null;
            _deadline = 0;
            _state = FrameDisplayState.Kept;
            _status = "已保留 GPU 全屏拉伸分辨率，关闭帧格时恢复。";
            return Result(true, _status);
        }

        return command switch
        {
            "restore" => Restore(""),
            "status" => Result(_state != FrameDisplayState.RestorePending, _status),
            _ => Result(false, "分辨率请求无效。"),
        };
    }

    private FrameDisplayReply Apply(uint width, uint height)
    {
        if (_state is FrameDisplayState.Preview or FrameDisplayState.RestorePending)
            return Result(false, "请先完成确认或恢复原分辨率。");
        if (display.Read() is not { } current)
            return Result(false, "无法读取主显示器的显示模式。使用复制显示时，请先改为扩展或仅显示一个屏幕。");
        if (_original is { } original && original.Target != current.Target)
            return Result(false, "主显示器已变更，请先恢复上一次使用的显示器。");
        if (display.FindStretch(current, width, height) is not { } selected)
            return Result(false, $"当前显示模式无法以 GPU 全屏拉伸应用 {width} × {height}。请尝试其他宽高。");
        if (selected.SameMode(current))
            return Result(false, "主显示器已经使用该 GPU 拉伸分辨率。");
        if (display.Read() is not { } latest || !latest.SameMode(current))
            return Result(false, "显示状态在检测时发生变化，请重新应用。");

        var hadOverride = _original is not null;
        _original ??= current;
        var code = display.Change(selected, latest);
        var changedAt = _timestamp();
        if (code != 0)
        {
            if (display.Read(current.Target) is { } after && after.SameMode(current))
            {
                if (!hadOverride) _original = null;
                return Result(false, $"Windows 拒绝了分辨率更改（代码 {code}）。");
            }
            return Restore($"Windows 拒绝了分辨率更改（代码 {code}）。") with { Success = false };
        }
        if (!ReadBack(selected.Target, selected.MatchesStretch))
            return Restore("驱动未按要求进行 GPU 全屏拉伸。") with { Success = false };

        _preview = selected;
        _deadline = changedAt + Stopwatch.Frequency * ConfirmationSeconds;
        _state = FrameDisplayState.Preview;
        _status = $"已临时切换到 {width} × {height}，GPU 全屏拉伸。";
        return Result(true, _status);
    }

    private void ExpirePreview()
    {
        if (_deadline != 0 && _timestamp() >= _deadline) Restore("15 秒内未确认。");
    }

    private FrameDisplayReply Restore(string reason)
    {
        _deadline = 0;
        _preview = null;
        if (_original is not { } original) return Result(true, _status);
        var current = display.Read(original.Target);
        if (current is null)
        {
            _state = FrameDisplayState.RestorePending;
            _status = reason + "无法读取原显示器。请重新连接后再关闭帧格。";
            return Result(false, _status);
        }
        if (!current.SameMode(original))
        {
            var code = display.Change(original, current);
            if (!ReadBack(original.Target, mode => mode.SameMode(original)))
            {
                _state = FrameDisplayState.RestorePending;
                _status = reason + $"未能核实原分辨率已恢复（Windows 代码 {code}）。请再次关闭帧格重试。";
                return Result(false, _status);
            }
        }
        _original = null;
        _state = FrameDisplayState.Idle;
        _status = reason + $"已恢复 {original.Width} × {original.Height}。";
        return Result(true, _status);
    }

    private bool ReadBack(FrameDisplayTarget target, Func<IFrameDisplayMode, bool> matches)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt != 0) Thread.Sleep(100);
            if (display.Read(target) is { } mode && matches(mode)) return true;
        }
        return false;
    }

    private FrameDisplayReply Result(bool success, string message) => new(success, _state, message, _deadline);
}
