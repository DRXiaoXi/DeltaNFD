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
            var videoInput = input with { Title = "v14安装不上", Description = "没做什么" };
            var videoIssues = BugFeedbackService.ValidateFields(videoInput);
            Check(videoIssues is [{ Field: BugFeedbackField.Description }], "视频案例准确定位描述框");
            Check(videoIssues[0].Message.Contains("当前 4 个字") && videoIssues[0].Message.Contains("还差 6 个字"), "视频案例明确当前字数与差额");
            Check(BugFeedbackService.DescriptionProgress(videoInput.Description).Contains("4/8000") &&
                BugFeedbackService.DescriptionProgress(videoInput.Description).Contains("还差 6 字"), "输入时显示同一计数规则");
            var allIssues = BugFeedbackService.ValidateFields(input with { Title = "", Area = "", Description = "", Contact = new string('x', 201) });
            Check(allIssues.Select(i => i.Field).SequenceEqual(new[] { BugFeedbackField.Title, BugFeedbackField.Area, BugFeedbackField.Description, BugFeedbackField.Contact }),
                "一次列出全部错误，并按表单顺序确定焦点");
            Check(allIssues.All(i => i.Message.Length > 0), "每个字段都有具体说明");
            Check(BugFeedbackService.Validate(input with { Title = "  两字  ", Description = "  一二三四五六七八九十  " }) == "", "去除首尾空白后达到最小长度可提交");
            Check(BugFeedbackService.ValidateFields(input with { Description = "一二三四五六七八九" }) is [{ Field: BugFeedbackField.Description }], "九字仍拦截");
            Check(BugFeedbackService.ValidateFields(input with { Description = new string('中', 8000) }).Count == 0, "描述长度上限可提交");
            Check(BugFeedbackService.ValidateFields(input with { Description = new string('中', 8001) }) is [{ Field: BugFeedbackField.Description }], "超长描述给出字段错误");
            Check(BugFeedbackService.ValidateFields(input with { Title = new string('中', 101) }) is [{ Field: BugFeedbackField.Title }], "超长标题给出字段错误");
            Check(BugFeedbackService.CharacterCount("  😀😀😀😀😀  ") == 5, "计数使用Unicode字符，与服务器字符数一致");
            Check(BugFeedbackService.ValidateFields(input with { Description = "😀😀😀😀😀" }) is [{ Field: BugFeedbackField.Description }], "不能将五个代理对字符误当十字");
            Check(BugFeedbackService.ValidateFields(input with { Description = "😀😀😀😀😀😀😀😀😀😀" }).Count == 0, "十个Unicode字符可提交");
            Check(BugFeedbackService.DescriptionProgress("   ").Contains("0/8000") &&
                BugFeedbackService.DescriptionProgress("一二三四五六七八九十").Contains("还差") == false, "补足字数后清除差额提示");
            var blockedBeforeRead = false;
            try { BugFeedbackService.Prepare(videoInput, root); }
            catch (ArgumentException ex) { blockedBeforeRead = ex.Message == videoIssues[0].Message; }
            Check(blockedBeforeRead, "不合格内容在收集日志或提交前拦截，并使用同一错误说明");
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
            foreach (var (status, detail) in new[]
            {
                (HttpStatusCode.BadRequest, "重新填写"), (HttpStatusCode.Forbidden, "联系作者"),
                (HttpStatusCode.TooManyRequests, "一分钟"), (HttpStatusCode.RequestEntityTooLarge, "取消附带日志"),
            })
            {
                using var rejected = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(status))));
                var failure = await BugFeedbackService.SubmitAsync(package, rejected, CancellationToken.None);
                Check(!failure.Success && failure.Message.Contains(detail), "失败原因包含可执行下一步：" + status);
            }
            using var badReceipt = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"accepted\",\"receipt\":\"wrong\"}") })));
            Check(!(await BugFeedbackService.SubmitAsync(package, badReceipt, CancellationToken.None)).Success, "错误回执拒绝");
            using var canceled = new HttpClient(new Handler(_ => throw new OperationCanceledException()));
            Check(!(await BugFeedbackService.SubmitAsync(package, canceled, CancellationToken.None)).Success, "取消结果保留未知，不伪报成功");
            using var disconnected = new HttpClient(new Handler(_ => throw new HttpRequestException()));
            var networkFailure = await BugFeedbackService.SubmitAsync(package, disconnected, CancellationToken.None);
            Check(!networkFailure.Success && networkFailure.Message.Contains("网络连接失败") && networkFailure.Message.Contains("同一编号"), "网络失败解释原因并保留同编号重试语义");
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
