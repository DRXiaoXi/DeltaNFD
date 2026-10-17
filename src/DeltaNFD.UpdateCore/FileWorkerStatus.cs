using System.Text.Json;

namespace DeltaNFD.Updates;

public sealed record FileWorkerStatus(string State, string Detail)
{
    // Never retain this handle while approving or awaiting the worker: Windows may
    // keep the old name pending deletion and deny its next atomic status update.
    public static FileWorkerStatus Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var json = JsonDocument.Parse(stream);
        return new(json.RootElement.GetProperty("state").GetString() ?? "", json.RootElement.GetProperty("detail").GetString() ?? "");
    }

    public static string Failure(FileWorkerStatus? status, int exitCode, string standardError)
    {
        if (status?.State == "Failed" && !string.IsNullOrWhiteSpace(status.Detail)) return status.Detail;
        var detail = standardError.Trim();
        if (detail.Length > 16000) detail = detail[..16000];
        return $"独立更新器退出（退出码 {exitCode}，状态 {status?.State ?? "未报告"}）"
            + (detail.Length > 0 ? "：" + detail : "；未完成安全交接，请查看本次会话的 handover.txt。");
    }
}
