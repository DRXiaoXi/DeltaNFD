using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DeltaNFD.Services;

internal static class BugFeedbackChecks
{
    internal static async Task RunAsync(string? fixturePath = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-feedback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var count = 0;
        void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); count++; }
        try
        {
            var input = new BugFeedbackInput("测试刷新异常", "其他", "点击刷新按钮后没有出现结果，预期显示检测结果。", "", true);
            Check(BugFeedbackService.Validate(input) == "", "有效问题可提交");
            Check(BugFeedbackService.Validate(input with { Title = " " }).Length > 0, "空标题拒绝");
            Check(BugFeedbackService.Validate(input with { Area = "未知" }).Length > 0, "未知模块拒绝");
            Check(BugFeedbackService.Validate(input with { Description = "短" }).Length > 0, "描述过短拒绝");
            Check(BugFeedbackService.Validate(input with { Contact = new string('a', 201) }).Length > 0, "联系方式长度受限");
            var redacted = BugFeedbackService.Redact("机器：TestPC · 用户：Alice · 管理员：是\nC:\\Users\\Alice\\private\\file.log\ntoken=super-secret\npassword: secret");
            Check(!redacted.Contains("Alice") && !redacted.Contains("TestPC") && !redacted.Contains("super-secret"), "路径、机器名和凭据去除");
            Check(redacted.Contains("private") && redacted.Contains("管理员"), "保留排障上下文");
            for (var i = 0; i < 5; i++)
            {
                var path = Path.Combine(root, "app_" + i + ".log");
                File.WriteAllText(path, i == 4 ? new string('a', BugFeedbackService.MaximumLogBytes + 50) + "\n末尾定位内容" : "测试日志 " + i);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5 + i));
            }
            File.WriteAllText(Path.Combine(root, "secrets.json"), "MUST-NOT-UPLOAD");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.WriteAllText(Path.Combine(root, "nested", "app_nested.log"), "NESTED-MUST-NOT-UPLOAD");
            var package = BugFeedbackService.Prepare(input, root);
            Check(package.LogCount == 3, "最多三个最近应用日志");
            Check(package.Preview.Contains("末尾定位内容") && package.Preview.Contains("仅包含末尾"), "大文件尾部读取且提示截断");
            Check(!package.Preview.Contains("MUST-NOT-UPLOAD"), "配置与子目录不收集");
            using (var zip = new ZipArchive(new MemoryStream(package.Archive)))
            {
                Check(zip.Entries.Count == 4 && zip.GetEntry("report.json") is not null, "固定 ZIP 合同");
                using var reader = new StreamReader(zip.GetEntry("report.json")!.Open());
                using var report = JsonDocument.Parse(reader.ReadToEnd());
                Check(report.RootElement.GetProperty("requestId").GetString() == package.RequestId.ToString("D"), "问题编号绑定快照");
            }
            var noLogs = BugFeedbackService.Prepare(input with { IncludeLogs = false }, root);
            Check(noLogs.LogCount == 0 && !noLogs.Preview.Contains("测试日志"), "不勾选时不附日志");
            Check(BugFeedbackService.Prepare(input, Path.Combine(root, "missing")).LogCount == 0, "缺日志时仍能提交描述");
            var attempts = 0;
            using var client = new HttpClient(new Handler(async request =>
            {
                attempts++;
                Check(request.RequestUri?.AbsoluteUri == BugFeedbackService.Endpoint && request.Method == HttpMethod.Post, "只发固定 HTTPS 端点");
                Check(request.Content?.Headers.ContentType?.MediaType == "application/zip", "固定上传类型");
                Check((await request.Content!.ReadAsByteArrayAsync()).SequenceEqual(package.Archive), "发送内容与预览快照一致");
                return new(HttpStatusCode.Created) { Content = new StringContent(JsonSerializer.Serialize(new { status = "accepted", receipt = "FB-" + package.RequestId.ToString("N").ToUpperInvariant() })) };
            }));
            Check((await BugFeedbackService.SubmitAsync(package, client, CancellationToken.None)).Success, "有效回执成功");
            Check((await BugFeedbackService.SubmitAsync(package, client, CancellationToken.None)).Success && attempts == 2, "重试沿用同一快照编号");
            foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.NotFound, HttpStatusCode.BadGateway, HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.Redirect })
            {
                using var rejected = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(status))));
                Check(!(await BugFeedbackService.SubmitAsync(package, rejected, CancellationToken.None)).Success, "错误和重定向不冒充成功：" + status);
            }
            using var badReceipt = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"accepted\",\"receipt\":\"wrong\"}") })));
            Check(!(await BugFeedbackService.SubmitAsync(package, badReceipt, CancellationToken.None)).Success, "错误回执拒绝");
            using var canceled = new HttpClient(new Handler(_ => throw new OperationCanceledException()));
            Check(!(await BugFeedbackService.SubmitAsync(package, canceled, CancellationToken.None)).Success, "取消结果保留未知，不伪报成功");
            if (fixturePath is not null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixturePath))!); File.WriteAllBytes(fixturePath, package.Archive); }
            Console.WriteLine($"Bug feedback client: {count} checks passed; synthetic logs and mock HTTP only. No real feedback uploaded.");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
}
