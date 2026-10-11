namespace DeltaNFD.Backends;

/// <summary>进程管理（模块 procman）：通用进程枚举/详情/能力读取与手动属性控制命令。</summary>
public static class ProcmanCommands
{
    // 只读：枚举 / 详情 / 能力 / 拓扑 / 状态与核对
    public const string ProcmanStatus = "procman.status";
    public const string ProcmanList = "procman.process.list";
    public const string ProcmanDetail = "procman.process.detail";
    public const string ProcmanCapability = "procman.process.capability";
    public const string ProcmanCpuTopology = "procman.cpu.topology";
    public const string ProcmanReconcile = "procman.reconcile";

    // 写入：修改 / 还原 / 释放
    public const string ProcmanSet = "procman.process.set";
    public const string ProcmanReset = "procman.process.reset";
    public const string ProcmanRelease = "procman.process.release";
    public const string ProcmanRestoreAll = "procman.restore-all";
    public const string ProcmanRecover = "procman.recover";

    // 规则（P2）
    public const string ProcmanRulesList = "procman.rules.list";
    public const string ProcmanRulesSave = "procman.rules.save";
    public const string ProcmanRulesDelete = "procman.rules.delete";

    // 后台抑制（P4）
    public const string ProcmanResponsivenessConfig = "procman.responsiveness.config";
    public const string ProcmanResponsivenessEvents = "procman.responsiveness.events";

    // 兼容层探针（P3 前置；只读）
    public const string ProcmanNativeProbe = "procman.native.probe";
}
