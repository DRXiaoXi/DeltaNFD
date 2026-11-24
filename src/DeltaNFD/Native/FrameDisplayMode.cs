using System.Runtime.InteropServices;

namespace DeltaNFD.Native;

internal readonly record struct FrameDisplayTarget(uint AdapterLow, int AdapterHigh, uint Id, string MonitorPath);

internal interface IFrameDisplayMode
{
    FrameDisplayTarget Target { get; }
    uint Width { get; }
    uint Height { get; }
    bool SameMode(IFrameDisplayMode other);
    bool MatchesStretch(IFrameDisplayMode actual);
}

internal interface IFrameDisplay
{
    IFrameDisplayMode? Read(FrameDisplayTarget? target = null);
    IFrameDisplayMode? FindStretch(IFrameDisplayMode current, uint width, uint height);
    int Change(IFrameDisplayMode mode, IFrameDisplayMode expected);
}

internal sealed partial class FrameDisplayMode : IFrameDisplay
{
    private const uint ActivePaths = 2, QueryVirtualModeAware = 0x10, QueryVirtualRefreshAware = 0x40;
    private const uint SuppliedConfig = 0x20, Validate = 0x40, Apply = 0x80;
    private const uint SetVirtualModeAware = 0x8000, SetVirtualRefreshAware = 0x20000;
    private const uint SupportsVirtualMode = 8, BoostRefreshRate = 16, Stretched = 3;
    private const int InvalidConfig = 1610, InsufficientBuffer = 122;
    private static readonly bool SupportsVirtualRefresh = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public IFrameDisplayMode? Read(FrameDisplayTarget? target = null) => Capture(target);

    public IFrameDisplayMode? FindStretch(IFrameDisplayMode current, uint width, uint height)
    {
        var baseline = (Snapshot)current;
        var path = baseline.Paths[baseline.Selected];
        // Indirect and wireless outputs cannot establish the GPU-to-monitor scaling contract.
        if (path.Target.OutputTechnology is 15 or 16 or 17 || path.Target.Available == 0)
            return null;

        var dimensions = RotatedSize(path.Target.Rotation, width, height);
        var signal = baseline.Modes[path.TargetIndex].Signal;
        if (signal.ActiveSize.Width == 0 || signal.ActiveSize.Height == 0 || dimensions == signal.ActiveSize)
            return null;

        var paths = (PathInfo[])baseline.Paths.Clone();
        var modes = (ModeInfo[])baseline.Modes.Clone();
        if (path.DesktopIndex >= 0)
        {
            if (modes[path.DesktopIndex].Desktop != DesktopImage.Full(baseline.Width, baseline.Height))
                return null;
            modes[path.DesktopIndex].Desktop = DesktopImage.Full(width, height);
        }
        modes[path.SourceIndex].Source.Width = width;
        modes[path.SourceIndex].Source.Height = height;
        paths[baseline.Selected].Target.Scaling = Stretched;
        var candidate = new Snapshot(paths, modes, baseline.Targets, baseline.Selected);
        return Set(candidate, Validate) == 0 ? candidate : null;
    }

    public int Change(IFrameDisplayMode mode, IFrameDisplayMode expected)
    {
        var config = (Snapshot)mode;
        var current = Capture(config.Target);
        if (current is null || !current.SameMode(expected) || !config.SameActivePaths(current)) return InvalidConfig;
        // SDC_SAVE_TO_DATABASE is omitted so the saved Windows display configuration stays intact.
        return Set(config, Apply);
    }

    private static Size2D RotatedSize(uint rotation, uint width, uint height) =>
        rotation is 2 or 4 ? new(height, width) : new(width, height);

    private static int Set(Snapshot config, uint operation) => SetDisplayConfig((uint)config.Paths.Length, config.Paths,
        (uint)config.Modes.Length, config.Modes, operation | SuppliedConfig | SetVirtualModeAware
        | (SupportsVirtualRefresh ? SetVirtualRefreshAware : 0));

    private static Snapshot? Capture(FrameDisplayTarget? target)
    {
        var flags = ActivePaths | QueryVirtualModeAware | (SupportsVirtualRefresh ? QueryVirtualRefreshAware : 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount) != 0
                || pathCount == 0 || modeCount == 0)
                return null;
            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            var code = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (code == InsufficientBuffer) continue;
            if (code != 0) return null;
            Array.Resize(ref paths, (int)pathCount);
            Array.Resize(ref modes, (int)modeCount);
            return Describe(paths, modes, target);
        }
        return null;
    }

    private static Snapshot? Describe(PathInfo[] paths, ModeInfo[] modes, FrameDisplayTarget? wanted)
    {
        var targets = new FrameDisplayTarget[paths.Length];
        var selected = -1;
        for (var i = 0; i < paths.Length; i++)
        {
            var path = paths[i];
            if (!ValidMode(modes, path.SourceIndex, 1, path.Source.Adapter, path.Source.Id)
                || !ValidMode(modes, path.TargetIndex, 2, path.Target.Adapter, path.Target.Id)
                || (path.DesktopIndex >= 0 && (path.DesktopIndex >= modes.Length || modes[path.DesktopIndex].Type != 3)))
                return null;

            var name = new TargetName
            {
                Header = new DeviceInfoHeader(2, (uint)Marshal.SizeOf<TargetName>(), path.Target.Adapter, path.Target.Id),
            };
            if (DisplayConfigGetDeviceInfo(ref name) != 0 || string.IsNullOrWhiteSpace(name.Path)) return null;
            targets[i] = new FrameDisplayTarget(path.Target.Adapter.Low, path.Target.Adapter.High, path.Target.Id, name.Path);
            var source = modes[path.SourceIndex].Source;
            if (wanted is { } identity ? targets[i] != identity : source.Position != default) continue;
            if (selected != -1) return null; // A mirrored primary source is shared by multiple targets.
            selected = i;
        }
        if (selected < 0 || paths.Count(p => p.Source.Adapter == paths[selected].Source.Adapter
            && p.Source.Id == paths[selected].Source.Id) != 1) return null;

        var config = new Snapshot(paths, modes, targets, selected);
        return config.Width > 0 && config.Height > 0 ? config : null;
    }

    private static bool ValidMode(ModeInfo[] modes, int index, uint type, AdapterId adapter, uint id) =>
        index >= 0 && index < modes.Length && modes[index].Type == type && modes[index].Adapter == adapter && modes[index].Id == id;

    private sealed class Snapshot(PathInfo[] paths, ModeInfo[] modes, FrameDisplayTarget[] targets, int selected) : IFrameDisplayMode
    {
        internal PathInfo[] Paths { get; } = paths;
        internal ModeInfo[] Modes { get; } = modes;
        internal FrameDisplayTarget[] Targets { get; } = targets;
        internal int Selected { get; } = selected;
        public FrameDisplayTarget Target => Targets[Selected];
        public uint Width => Modes[Paths[Selected].SourceIndex].Source.Width;
        public uint Height => Modes[Paths[Selected].SourceIndex].Source.Height;

        public bool SameMode(IFrameDisplayMode other) => other is Snapshot config && Matches(config, comparePositions: true);

        public bool MatchesStretch(IFrameDisplayMode actual)
        {
            if (actual is not Snapshot config || !Matches(config, comparePositions: false)) return false;
            var path = config.Paths[config.Selected];
            var activeSignal = config.Modes[path.TargetIndex].Signal.ActiveSize;
            return path.Target.Scaling == Stretched
                && activeSignal != RotatedSize(path.Target.Rotation, config.Width, config.Height);
        }

        internal bool SameActivePaths(Snapshot other)
        {
            if (Paths.Length != other.Paths.Length || Target != other.Target) return false;
            for (var i = 0; i < Paths.Length; i++)
            {
                var j = Array.IndexOf(other.Targets, Targets[i]);
                if (j < 0 || Paths[i].Source.Adapter != other.Paths[j].Source.Adapter
                    || Paths[i].Source.Id != other.Paths[j].Source.Id)
                    return false;
            }
            return true;
        }

        private bool Matches(Snapshot other, bool comparePositions)
        {
            if (!SameActivePaths(other)) return false;
            for (var i = 0; i < Paths.Length; i++)
            {
                var left = Paths[i];
                var right = other.Paths[Array.IndexOf(other.Targets, Targets[i])];
                var source = Modes[left.SourceIndex].Source;
                var otherSource = other.Modes[right.SourceIndex].Source;
                if (!comparePositions) source.Position = otherSource.Position;
                if (!source.Equals(otherSource)
                    || !Modes[left.TargetIndex].Signal.Equals(other.Modes[right.TargetIndex].Signal)
                    || left.Target.Scaling != right.Target.Scaling || left.Target.Rotation != right.Target.Rotation
                    || left.Target.RefreshRate != right.Target.RefreshRate
                    || left.Target.ScanLineOrdering != right.Target.ScanLineOrdering
                    || (left.Flags & BoostRefreshRate) != (right.Flags & BoostRefreshRate)
                    || DesktopFor(left) != other.DesktopFor(right))
                    return false;
            }
            return true;
        }

        private DesktopImage? DesktopFor(PathInfo path) => path.DesktopIndex < 0 ? null : Modes[path.DesktopIndex].Desktop;
    }
}
