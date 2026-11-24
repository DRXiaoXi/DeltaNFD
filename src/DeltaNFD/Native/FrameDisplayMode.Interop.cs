using System.Runtime.InteropServices;

namespace DeltaNFD.Native;

internal sealed partial class FrameDisplayMode
{
    [StructLayout(LayoutKind.Sequential)] private readonly record struct AdapterId(uint Low, int High);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Size2D(uint Width, uint Height);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Rational(uint Numerator, uint Denominator);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Rectangle(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public PathSource Source;
        public PathTarget Target;
        public uint Flags;
        private bool VirtualMode => (Flags & SupportsVirtualMode) != 0;
        public int SourceIndex => VirtualMode ? VirtualIndex(Source.ModeIndex >> 16) : unchecked((int)Source.ModeIndex);
        public int TargetIndex => VirtualMode ? VirtualIndex(Target.ModeIndex >> 16) : unchecked((int)Target.ModeIndex);
        public int DesktopIndex => VirtualMode ? VirtualIndex(Target.ModeIndex & 0xffff) : -1;
        private static int VirtualIndex(uint value) => value == 0xffff ? -1 : (int)value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSource
    {
        public AdapterId Adapter;
        public uint Id, ModeIndex, StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTarget
    {
        public AdapterId Adapter;
        public uint Id, ModeIndex, OutputTechnology, Rotation, Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int Available;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct ModeInfo
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public AdapterId Adapter;
        [FieldOffset(16)] public SourceMode Source;
        [FieldOffset(16)] public SignalInfo Signal;
        [FieldOffset(16)] public DesktopImage Desktop;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceMode
    {
        public uint Width, Height, PixelFormat;
        public Point Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SignalInfo
    {
        public ulong PixelRate;
        public Rational HSync, VSync;
        public Size2D ActiveSize, TotalSize;
        public uint Standard, ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DesktopImage(Point SourceSize, Rectangle Region, Rectangle Clip)
    {
        internal static DesktopImage Full(uint width, uint height)
        {
            var bounds = new Rectangle(0, 0, (int)width, (int)height);
            return new DesktopImage(new Point((int)width, (int)height), bounds, bounds);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DeviceInfoHeader(uint Type, uint Size, AdapterId Adapter, uint Id);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetName
    {
        public DeviceInfoHeader Header;
        public uint Flags, OutputTechnology;
        public ushort ManufacturerId, ProductId;
        public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Path;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths,
        ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topologyId);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetDisplayConfig(uint pathCount, [In] PathInfo[] paths,
        uint modeCount, [In] ModeInfo[] modes, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetName request);
}
