using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>Explicit driver-name reconstruction. Automatic/frame restore never invokes this path.</summary>
internal sealed class GpuDriverNameRecovery(
    Func<string, GpuDriverNameSource.Evidence> read,
    Action<GpuDriverNameSource.Evidence> saveEvidence,
    Action<GpuDriverNameSource.Evidence> writeAndVerify,
    Action<GpuDriverNameSource.Evidence> completePending)
{
    internal sealed class BeforeWriteException(string message) : IOException(message);
    internal GpuDriverNamePreview Preview(string path)
    {
        if (!GpuDriverNameSource.ValidPath(path)) throw new IOException("所选显卡路径无效。");
        return read(path).Preview;
    }

    internal OperationResult Restore(string path, string fingerprint)
    {
        var submitted = false;
        try
        {
            if (!GpuDriverNameSource.ValidPath(path) || fingerprint is null || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
                return OperationResult.Fail("恢复确认无效，请重新读取当前驱动名称。");
            var current = read(path);
            if (!current.RegistryPath.Equals(path, StringComparison.OrdinalIgnoreCase) || !current.Fingerprint.Equals(fingerprint, StringComparison.Ordinal))
                return OperationResult.Fail("显卡、驱动、当前名称或待还原任务已变化，请重新预览确认；未写入。");
            saveEvidence(current); // Durable current value before any registry/ACL writes.
            if (!read(path).Fingerprint.Equals(fingerprint, StringComparison.Ordinal))
                return OperationResult.Fail("保存恢复证据期间目标已变化，未写入；证据保留。");
            submitted = true;
            writeAndVerify(current);
            completePending(current);
            return OperationResult.Ok($"已按当前驱动重建为「{current.DriverDescription}」。这不是找回旧备份，名称可能与旧驱动时期不同；重启电脑后生效。", requiresReboot: true);
        }
        catch (BeforeWriteException ex)
        {
            return OperationResult.Fail("执行前检查已停止恢复，未改写名称或权限：" + ex.Message);
        }
        catch (Exception ex)
        {
            return new OperationResult { Success = false, IsPartial = submitted, OutcomeUnknown = submitted,
                Message = submitted ? "按驱动恢复未完整确认，写前证据保留，请核对当前名称和待还原记录：" + ex.Message
                    : "无法核验驱动提供的真实型号，未写入：" + ex.Message };
        }
    }

    internal static GpuDriverNameRecovery Create(TweakBackupStore backups) => new(
        path => GpuDriverNameSource.Read(path, backups),
        evidence =>
        {
            var directory = Path.Combine(AppDataPaths.Root, "GpuDriverNameRecovery", Guid.NewGuid().ToString("N"));
            GpuDriverNameSource.RejectLinks(directory); Directory.CreateDirectory(directory); GpuDriverNameSource.RejectLinks(directory);
            using var file = new FileStream(Path.Combine(directory, "before.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(file, new { schemaVersion = 1, purpose = "按当前驱动重建；CurrentDescription不是历史原型号备份", createdUtc = DateTimeOffset.UtcNow, evidence });
            file.Flush(true);
            Log.Info("按驱动恢复型号：写前证据已保存 " + directory);
        },
        evidence =>
        {
            try
            {
                if (DeltaForceLocator.IsGameRunning()) throw new BeforeWriteException("三角洲正在运行。");
            }
            catch (BeforeWriteException) { throw; }
            catch (Exception ex) { throw new BeforeWriteException("无法确认三角洲运行状态：" + ex.Message); }
            RegistryAclHelper.EnsureWritable(evidence.RegistryPath);
            using var key = Registry.LocalMachine.OpenSubKey(evidence.RegistryPath, writable: true)
                ?? throw new IOException("无法打开所选显卡实例。");
            // ACL work is not permission to use stale driver data or overwrite a newly created backup.
            if (GpuDriverNameSource.Read(evidence.RegistryPath, backups).Fingerprint != evidence.Fingerprint)
                throw new IOException("驱动绑定或原始备份已变化，未写入名称。");
            if (DeltaForceLocator.IsGameRunning()) throw new IOException("三角洲已启动，未写入显卡名称。");
            key.SetValue("DeviceDesc", evidence.Description, RegistryValueKind.String);
            using var verify = Registry.LocalMachine.OpenSubKey(evidence.RegistryPath) ?? throw new IOException("写后设备已不存在。");
            if (verify.GetValue("DeviceDesc") as string != evidence.Description) throw new IOException("名称写后复读不一致。");
            var after = GpuDriverNameSource.Read(evidence.RegistryPath, backups);
            if (after with { CurrentDescription = evidence.CurrentDescription } != evidence)
                throw new IOException("名称写入期间设备、驱动或待还原任务已变化。");
        },
        evidence =>
        {
            GpuSpoofRestoreState.Complete(evidence.Pending, evidence.RegistryPath);
            try { GpuSpoofWatch.Remove(evidence.RegistryPath); }
            catch (Exception ex) { Log.Warn("驱动名称已核验恢复，但旧伪装监测清理失败：" + ex.Message); }
        });
}
