namespace DeltaNFD.Execution.Procman;

/// <summary>
/// 按「进程身份 + 属性」划分的控制权协调器：同一进程的同一属性同一时刻只能有一个控制方。
/// 控制方：manual（用户手动）/ rule:名称（持久规则）/ responsiveness（仅保留旧版后台抑制的恢复身份）。
/// 游戏模式（CPU/帧格引擎、脱机助手）属于跨进程的控制方，通过 <see cref="DeclareExternalOwner"/>
/// 登记——它们不运行在本进程内，但属性所有权照样必须协调。
///
/// 写入流程（按清单）：检查归属 → 登记写前意图 → 执行 → 回读。旧控制方的迟到写入由代次失效：
/// 每次所有权变化使全局代次 +1，写入方持有执行时的代次，提交前代次不符即放弃。
/// </summary>
public sealed class ProcmanControlCoordinator
{
    public const string OwnerSystem = "system";
    public const string OwnerManual = "manual";
    public const string OwnerResponsiveness = "responsiveness";
    public const string PrefixRule = "rule:";

    private sealed record Lease(string Owner, long Generation);

    private readonly object _gate = new();
    private readonly Dictionary<string, Lease> _leases = []; // key = ChangeKey(pid,start,path,capability)
    private long _generation;

    /// <summary>外部控制方登记（游戏帧格/脱机助手在目标进程属性上的控制声明）。</summary>
    private readonly Dictionary<string, string> _externalOwners = [];

    public static string Key(int pid, long startTicks, string path, string capability)
        => ProcmanChangeLog.ChangeKey(pid, startTicks, path, capability);

    /// <summary>当前控制代次（所有权每次变化自增；写入方提交前核对）。</summary>
    public long Generation { get { lock (_gate) return _generation; } }

    /// <summary>声明外部控制方对进程属性的持续控制（游戏模式/脱机助手）。重复声明幂等。</summary>
    public void DeclareExternalOwner(int pid, long startTicks, string path, string capability, string owner)
    {
        if (owner == OwnerSystem) throw new ArgumentException("system 不是可登记的控制方。");
        lock (_gate) _externalOwners[Key(pid, startTicks, path, capability)] = owner;
    }

    /// <summary>撤销外部控制方声明（游戏模式还原/脱机退出时）。</summary>
    public void ClearExternalOwner(int pid, long startTicks, string path, string capability)
    {
        lock (_gate) _externalOwners.Remove(Key(pid, startTicks, path, capability));
    }

    /// <summary>当前控制方：本协调器租约 > 外部声明 > system（无人控制）。</summary>
    public string OwnerOf(int pid, long startTicks, string path, string capability)
    {
        var key = Key(pid, startTicks, path, capability);
        lock (_gate)
            return _leases.TryGetValue(key, out var lease) ? lease.Owner
                : _externalOwners.TryGetValue(key, out var external) ? external
                : OwnerSystem;
    }

    /// <summary>是否被游戏模式类外部控制方持有（用于前端「已由 XX 控制，接管需确认」）。</summary>
    public bool IsExternallyOwned(int pid, long startTicks, string path, string capability)
    {
        lock (_gate)
        {
            if (_leases.ContainsKey(Key(pid, startTicks, path, capability))) return false;
            return _externalOwners.ContainsKey(Key(pid, startTicks, path, capability));
        }
    }

    /// <summary>
    /// 尝试获取属性控制权。已有租约且归属他人时失败（返回 false 并给出当前控制方）。
    /// 外部控制方持有（游戏模式）时同样失败——用户手动接管必须走显式 Takeover（带确认）。
    /// </summary>
    public bool TryAcquire(int pid, long startTicks, string path, string capability, string owner, out string currentOwner)
    {
        var key = Key(pid, startTicks, path, capability);
        lock (_gate)
        {
            currentOwner = OwnerOf(pid, startTicks, path, capability);
            if (currentOwner != OwnerSystem && currentOwner != owner) return false;
            var added = !_leases.ContainsKey(key);
            _leases[key] = new Lease(owner, _generation);
            if (added) _generation++;
            return true;
        }
    }

    /// <summary>显式接管（用户已确认）：替换现有控制方。返回被替换的原控制方（system 表示无冲突）。</summary>
    public string Takeover(int pid, long startTicks, string path, string capability, string owner)
    {
        var key = Key(pid, startTicks, path, capability);
        lock (_gate)
        {
            var previous = OwnerOf(pid, startTicks, path, capability);
            _leases[key] = new Lease(owner, ++_generation);
            _externalOwners.Remove(key); // 接管后原外部控制方对本属性失效
            return previous;
        }
    }

    /// <summary>释放控制权：移除租约并返回原控制方（供调用方交还）。system = 本就没有租约。</summary>
    public string Release(int pid, long startTicks, string path, string capability)
    {
        var key = Key(pid, startTicks, path, capability);
        lock (_gate)
        {
            if (!_leases.Remove(key)) return OwnerSystem;
            _generation++;
            return OwnerSystem;
        }
    }

    /// <summary>提交前核对：写入方持有的代次是否仍有效（所有权变化使旧写入任务失效）。</summary>
    public bool IsGenerationValid(long generation)
    {
        lock (_gate) return generation == _generation;
    }
}
