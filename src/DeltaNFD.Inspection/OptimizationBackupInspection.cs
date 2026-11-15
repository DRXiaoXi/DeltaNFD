using System.Text.Json;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;

namespace DeltaNFD.Inspection;
public static class OptimizationBackupInspection
{
    public static Task<BxBackupStatus> ReadAsync() => InspectionScheduler.Run(() =>
    {
        var path = Path.Combine(AppDataPaths.Root, "optimize_backup.json");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 8 * 1024 * 1024)
                throw new InvalidDataException("备份文件过大，未读取为正常状态。");
            var file = JsonSerializer.Deserialize<BxBackupFile>(stream) ?? throw new InvalidDataException("备份内容为空。");
            return new BxBackupStatus
            {
                Readable = true,
                Count = file.Entries.Count,
                Entries = file.Entries
            };
        }
        catch (FileNotFoundException)
        {
            return new BxBackupStatus
            {
                Readable = true
            };
        }
        catch (DirectoryNotFoundException)
        {
            return new BxBackupStatus
            {
                Readable = true
            };
        }
        catch (Exception ex)
        {
            return new BxBackupStatus
            {
                Readable = false,
                Error = ex.Message
            };
        }
    });
}
