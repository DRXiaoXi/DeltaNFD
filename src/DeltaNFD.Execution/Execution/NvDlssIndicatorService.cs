using DeltaNFD.Inspection;
using Microsoft.Win32;

namespace DeltaNFD.Services;

internal static class NvDlssIndicatorService
{
    internal static OperationResult Set(bool enabled)
    {
        if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(NvDlssIndicatorInspection.KeyPath, writable: true);
            if (key is null) return OperationResult.Fail("未找到 NVIDIA NGXCore 注册表项，未写入。");
            return Commit(enabled, () => NvDlssIndicatorInspection.Read(key),
                value => key.SetValue(NvDlssIndicatorInspection.ValueName, value, RegistryValueKind.DWord),
                NvDlssIndicatorInspection.Read);
        }
        catch (Exception ex) { return OperationResult.Fail("无法打开 DLSS 指示器设定，未写入：" + ex.Message); }
    }

    internal static OperationResult Commit(bool enabled, Func<NvDlssIndicatorStatus> readBefore,
        Action<int> write, Func<NvDlssIndicatorStatus> readAfter)
    {
        var attempted = false;
        var label = enabled ? "开启" : "关闭";
        OperationResult Unconfirmed(string detail) => new()
        {
            OutcomeUnknown = true,
            Message = "未能确认 DLSS 指示器保存结果。请刷新核对，不要重复提交。" + detail,
        };
        try
        {
            var before = readBefore();
            if (!before.CanSet) return OperationResult.Fail(before.Detail + " 未写入。");
            var raw = enabled ? NvDlssIndicatorInspection.ReleaseEnabled : 0;
            if (before.Enabled == enabled)
                return OperationResult.Skip($"DLSS 指示器已{label}，未重复写入。若游戏已打开，请完整退出后重进。");
            attempted = true;
            write(raw);
            var actual = readAfter();
            if (actual.Enabled != enabled || actual.StoredValue != (uint)raw) return Unconfirmed(actual.Detail);
            return OperationResult.Ok($"已保存并核验 DLSS 指示器：{label}。若游戏已打开，请完整退出后重进。");
        }
        catch (Exception ex)
        {
            return attempted ? Unconfirmed(ex.Message) : OperationResult.Fail("DLSS 指示器原状态读取失败，未写入：" + ex.Message);
        }
    }
}
