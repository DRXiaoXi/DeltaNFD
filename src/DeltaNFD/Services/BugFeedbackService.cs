using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

public sealed record BugFeedbackInput(string Title, string Area, string Description, string Contact, bool IncludeLogs);
public sealed record BugFeedbackPackage(Guid RequestId, byte[] Archive, string Preview, int LogCount);
public sealed record BugFeedbackResult(bool Success, string Message, string Receipt = "");
public enum BugFeedbackField { Title, Area, Description, Contact }
public sealed record BugFeedbackValidationIssue(BugFeedbackField Field, string Message);

/// <summary>只在玩家确认后发送。预览与发送使用同一份不可变快照；不上传配置、系统文件或任意附件。</summary>
public static class BugFeedbackService
{
    public const string Endpoint = "https://deltaxiaoxi.xin/api/feedback";
    public const int MaximumArchiveBytes = 4 * 1024 * 1024;
    internal const int MaximumLogBytes = 256 * 1024;
    public const int MinimumTitleCharacters = 2;
    public const int MaximumTitleCharacters = 100;
    public const int MinimumDescriptionCharacters = 10;
    public const int MaximumDescriptionCharacters = 8000;
    public static readonly string[] Areas = ["运行库", "内存回收", "帧格", "ACE相关", "着色器", "NVIDIA设置", "进程管理", "自动更新", "其他"];
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };

    public static int CharacterCount(string text) => text.Trim().EnumerateRunes().Count();

    public static string DescriptionProgress(string text)
    {
        var count = CharacterCount(text);
        return $"已填写 {count}/{MaximumDescriptionCharacters} 字（首尾空白不计）" +
            (count < MinimumDescriptionCharacters ? $"，至少需要 {MinimumDescriptionCharacters} 字，还差 {MinimumDescriptionCharacters - count} 字。" : "。");
    }

    public static IReadOnlyList<BugFeedbackValidationIssue> ValidateFields(BugFeedbackInput input)
    {
        var issues = new List<BugFeedbackValidationIssue>();
        var titleCount = CharacterCount(input.Title);
        if (titleCount < MinimumTitleCharacters)
            issues.Add(new(BugFeedbackField.Title, $"问题标题至少需要 {MinimumTitleCharacters} 个字，当前 {titleCount} 个字。请概括遇到的问题。"));
        else if (titleCount > MaximumTitleCharacters)
            issues.Add(new(BugFeedbackField.Title, $"问题标题最多 {MaximumTitleCharacters} 个字，当前 {titleCount} 个字。请缩短标题。"));
        if (!Areas.Contains(input.Area)) issues.Add(new(BugFeedbackField.Area, "请选择问题所在模块；不确定时可以选择“其他”。"));
        var descriptionCount = CharacterCount(input.Description);
        if (descriptionCount < MinimumDescriptionCharacters)
            issues.Add(new(BugFeedbackField.Description, $"问题描述至少需要 {MinimumDescriptionCharacters} 个字，当前 {descriptionCount} 个字，还差 {MinimumDescriptionCharacters - descriptionCount} 个字。请补充操作步骤、实际结果和预期结果。"));
        else if (descriptionCount > MaximumDescriptionCharacters)
            issues.Add(new(BugFeedbackField.Description, $"问题描述最多 {MaximumDescriptionCharacters} 个字，当前 {descriptionCount} 个字。请删减后再试。"));
        if (input.Contact.Length > 200) issues.Add(new(BugFeedbackField.Contact, "联系方式最多 200 字，请缩短或留空。"));
        return issues;
    }

    public static string Validate(BugFeedbackInput input) => ValidateFields(input).FirstOrDefault()?.Message ?? "";

    public static BugFeedbackPackage Prepare(BugFeedbackInput input) => Prepare(input, Log.LogDirectory);

    internal static BugFeedbackPackage Prepare(BugFeedbackInput input, string logDirectory)
    {
        var error = Validate(input);
        if (error.Length > 0) throw new ArgumentException(error);
        var id = Guid.NewGuid();
        var entries = new List<(string Name, string Text)>();
        var notices = new List<string>();
        if (input.IncludeLogs)
        {
            if (Directory.Exists(logDirectory))
            {
                for (var directory = new DirectoryInfo(logDirectory); directory is not null; directory = directory.Parent)
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("日志目录含链接，请先打开日志文件夹核对。未收集日志。" );
                var files = new DirectoryInfo(logDirectory).EnumerateFiles("app_*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastWriteTimeUtc).Take(3).ToArray();
                foreach (var file in files)
                {
                    try
                    {
                        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) { notices.Add("跳过链接日志文件。"); continue; }
                        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var length = stream.Length;
                        stream.Position = Math.Max(0, length - MaximumLogBytes);
                        var buffer = new byte[(int)Math.Min(length, MaximumLogBytes)];
                        var read = 0;
                        while (read < buffer.Length) { var count = stream.Read(buffer, read, buffer.Length - read); if (count == 0) break; read += count; }
                        var text = Encoding.UTF8.GetString(buffer, 0, read);
                        if (length > MaximumLogBytes) text = "[日志过大，仅包含末尾 256 KiB]\n" + text;
                        entries.Add(($"logs/log-{entries.Count + 1:00}.txt", Redact(text)));
                    }
                    catch (IOException) { notices.Add("一份日志无法读取，已跳过。"); }
                    catch (UnauthorizedAccessException) { notices.Add("一份日志无读取权限，已跳过。"); }
                }
            }
            if (entries.Count == 0) notices.Add("未收集到可读取的应用日志，本次只发送问题描述。");
        }
        var report = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, requestId = id.ToString("D"), createdUtc = DateTime.UtcNow,
            appVersion = AppVersion.Text, osVersion = Environment.OSVersion.VersionString,
            title = input.Title.Trim(), area = input.Area, description = input.Description.Trim(), contact = input.Contact.Trim(),
            logCount = entries.Count, notices,
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var entry in new[] { (Name: "report.json", Text: report) }.Concat(entries))
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry.Name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
                writer.Write(entry.Text);
            }
        }
        var bytes = memory.ToArray();
        if (bytes.Length > MaximumArchiveBytes) throw new IOException("反馈包超过 4 MiB，请取消附带日志后重新提交。");
        var preview = report + string.Concat(entries.Select(e => "\n\n--- " + e.Name + " ---\n" + e.Text));
        return new(id, bytes, preview, entries.Count);
    }

    internal static string Redact(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"(?i)[a-z]:[\\/]Users[\\/][^\\/\r\n\s""<>]+", "%USERPROFILE%");
        text = Regex.Replace(text, @"(?m)(机器：|用户：)[^·\r\n]+", "$1[已隐藏] ");
        return Regex.Replace(text, @"(?i)(\b(?:access_token|token|password|passwd|secret|api_key|authorization)\b[""']?\s*[:=]\s*)[^\r\n]+", "$1[已隐藏]");
    }

    public static Task<BugFeedbackResult> SubmitAsync(BugFeedbackPackage package, CancellationToken cancellationToken) => SubmitAsync(package, Client, cancellationToken);

    internal static async Task<BugFeedbackResult> SubmitAsync(BugFeedbackPackage package, HttpClient client, CancellationToken cancellationToken)
    {
        if (package.RequestId == Guid.Empty || package.Archive.Length is < 1 or > MaximumArchiveBytes)
            return new(false, "反馈包无效，请重新生成预览。");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Content = new ByteArrayContent(package.Archive);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                if (response.Content.Headers.ContentLength > 8192) return new(false, "服务器响应异常，请保留此反馈后重试。");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[8193];
                var count = 0;
                while (count < buffer.Length) { var n = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken); if (n == 0) break; count += n; }
                if (count > 8192) return new(false, "服务器响应过大，请稍后重试。");
                using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
                var root = json.RootElement;
                var expected = "FB-" + package.RequestId.ToString("N").ToUpperInvariant();
                if (!root.TryGetProperty("status", out var status) || status.GetString() != "accepted"
                    || !root.TryGetProperty("receipt", out var receipt) || receipt.GetString() != expected)
                    return new(false, "服务器未返回有效反馈编号，可能已收到；可重试同一反馈核对。");
                return new(true, "反馈已收到，编号：" + expected + "。请保留编号，方便后续联系作者。", expected);
            }
            return new(false, response.StatusCode switch
            {
                HttpStatusCode.BadRequest => "服务器拒绝了反馈格式或内容（HTTP 400）。请重新填写并生成预览；仍然失败时请联系作者。",
                HttpStatusCode.Forbidden => "服务器拒绝访问（HTTP 403）。请稍后重试，持续出现时请联系作者。",
                HttpStatusCode.NotFound or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable => "反馈服务暂不可用，请稍后重试。",
                HttpStatusCode.TooManyRequests => "提交较频繁，请稍等一分钟后重试。",
                HttpStatusCode.RequestEntityTooLarge => "反馈包超过服务器限制，请取消附带日志后重新提交。",
                HttpStatusCode.Conflict => "反馈编号发生冲突，请重新填写生成新的反馈。",
                _ => "提交未完成（HTTP " + (int)response.StatusCode + "），请保留当前反馈并稍后重试。",
            });
        }
        catch (OperationCanceledException) { return new(false, "发送已取消或超时，服务器可能已收到；重试会沿用同一编号，避免重复记录。"); }
        catch (HttpRequestException) { return new(false, "网络连接失败，请检查网络后重试。服务器可能已收到，重试会沿用同一编号。"); }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        { return new(false, "未能确认服务器回执，请保留当前反馈并稍后重试。"); }
    }
}
