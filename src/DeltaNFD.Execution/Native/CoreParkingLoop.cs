using System.Runtime.InteropServices;

namespace DeltaNFD.Native;

// ---------------- 核心常驻轮询环（活动式防核心停放） ----------------
// 原理：后台线程高频（100ms）查询逐核「Parking Status」性能计数器并做轻量活动，
// 同时每 tick 把自身亲和性轮转到下一个逻辑核心——每个核心都被周期性唤醒，
// 来不及进入深度停靠。「靠活动而非靠电源策略」，完全不写任何电源计划设置。
// 纯运行时行为：Stop 即恢复亲和性，零残留。

/// <summary>核心停放状态轮询环。回调在轮询线程触发（调用方自行封送到 UI）。</summary>
public sealed class CoreParkingLoop : IDisposable
{
    private const int TickMs = 100;
    private const uint PdhFmtLong = 0x00000200;
    private const int PdhMoreData = unchecked((int)0x800007D2);
    private const uint ErrorSuccess = 0;

    // PDH_FMT_COUNTERVALUE_ITEM_W：LPWSTR szName(8) + { DWORD CStatus(4)+pad(4) + union(8) } = 24 字节
    private const int ItemSize = 24;
    private const int ValueOffset = 16; // FmtValue 联合体（longValue）在条目内的偏移

    [DllImport("pdh.dll")]
    private static extern int PdhOpenQueryW(IntPtr dataSource, uint userData, out IntPtr query);

    [DllImport("pdh.dll")]
    private static extern int PdhAddEnglishCounterW(IntPtr query, [MarshalAs(UnmanagedType.LPWStr)] string counterPath, uint userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);

    private const string ParkingStatusPath = @"\Processor Information(*)\Parking Status";

    private Thread? _thread;
    private volatile bool _stop;
    private IntPtr _query = IntPtr.Zero;
    private IntPtr _counter = IntPtr.Zero;
    private bool _counterUsable;
    private int _lastParkedCount = -1;
    private readonly int _lpCount = Environment.ProcessorCount;

    /// <summary>停靠核心数变化回调（参数 = 停靠核心数；null = 计数器不可用）。在轮询线程触发。</summary>
    private Action<int?>? _onParkedChanged;

    /// <summary>是否正在运行。</summary>
    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>启动轮询环（已在运行则忽略）。</summary>
    public void Start(Action<int?> onParkedChanged)
    {
        if (IsRunning)
        {
            return;
        }

        _onParkedChanged = onParkedChanged;
        _stop = false;
        _thread = new Thread(Loop)
        {
            Name = "ResponseBoostCoreParkingLoop",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>停止轮询环并还原线程亲和性。</summary>
    public void Stop()
    {
        _stop = true;
        _thread?.Join(1000);
        _thread = null;
        CloseQuery();
    }

    private void Loop()
    {
        OpenQuery();

        var core = 0;
        var defaultMask = (ulong)System.Diagnostics.Process.GetCurrentProcess().ProcessorAffinity;
        try
        {
            while (!_stop)
            {
                if (_counterUsable)
                {
                    var parked = CollectParkedCount();
                    if (parked is not null && parked != _lastParkedCount)
                    {
                        _lastParkedCount = parked.Value;
                        try
                        {
                            _onParkedChanged?.Invoke(parked);
                        }
                        catch
                        {
                            // 回调失败不影响轮询
                        }
                    }
                }

                // 亲和性轮转：本 tick 钉到下一个逻辑核心，保证每个核心都被周期性唤醒
                if (_lpCount > 0 && _lpCount < 64)
                {
                    var mask = 1UL << core;
                    SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)mask);
                    core = (core + 1) % _lpCount;
                }

                Thread.Sleep(TickMs);
            }
        }
        catch
        {
            // 轮询环异常退出不影响其他功能
        }
        finally
        {
            // 还原线程亲和性到进程默认（全核心）
            try
            {
                SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)defaultMask);
            }
            catch
            {
                // 线程可能已无意义，忽略
            }

            CloseQuery();
        }
    }

    private void OpenQuery()
    {
        try
        {
            if (PdhOpenQueryW(IntPtr.Zero, 0, out _query) != ErrorSuccess)
            {
                return;
            }

            if (PdhAddEnglishCounterW(_query, ParkingStatusPath, 0, out _counter) != ErrorSuccess)
            {
                // 本系统没有 Parking Status 计数器：退化为纯活动轮询（防停靠效果依旧）
                return;
            }

            // 先收集一次确认数据可用
            if (PdhCollectQueryData(_query) == ErrorSuccess)
            {
                _counterUsable = true;
            }
        }
        catch
        {
            _counterUsable = false;
        }
    }

    private void CloseQuery()
    {
        if (_query != IntPtr.Zero)
        {
            try
            {
                PdhCloseQuery(_query);
            }
            catch
            {
                // 忽略关闭失败
            }

            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
            _counterUsable = false;
        }
    }

    /// <summary>收集一次逐核停放状态，返回停靠核心数（不含 _Total 实例）；失败返回 null。</summary>
    private int? CollectParkedCount()
    {
        if (PdhCollectQueryData(_query) != ErrorSuccess)
        {
            return null;
        }

        var size = 0u;
        var count = 0u;
        var status = PdhGetFormattedCounterArrayW(_counter, PdhFmtLong, ref size, ref count, IntPtr.Zero);
        if (status != PdhMoreData || size == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArrayW(_counter, PdhFmtLong, ref size, ref count, buffer);
            if (status != ErrorSuccess)
            {
                return null;
            }

            var parked = 0;
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * ItemSize;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                if (name is null || name.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Parking Status：1 = 该核心已停靠
                if (Marshal.ReadInt32(item, ValueOffset) == 1)
                {
                    parked++;
                }
            }

            return parked;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose() => Stop();
}
