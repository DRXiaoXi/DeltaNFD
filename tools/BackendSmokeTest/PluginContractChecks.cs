using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DeltaNFD.Services.Plugins;

namespace BackendSmokeTest;

/// <summary>
/// 拓展插件规范 1.0 合同验证（待做清单第六节第 1 项）。
/// 纯逻辑检查：manifest/ui/IPC 解析器的合法与非法用例、路径校验、信任文案，
/// 以及真实启动示例插件后端完成 hello/ready 握手与一次只读 report 端到端。
/// 除临时目录与测试子进程外不触碰系统；不导入 .dnfdplugin、不安装插件。
/// </summary>
internal static class PluginContractChecks
{
    private static int _failures;
    private static int _checks;

    public static void Run(string[] args)
    {
        // --plugin-contract-e2e-checks：需要先构建示例工程，执行真实后端端到端。
        var exampleEndToEnd = Array.IndexOf(args, "--plugin-example-e2e") >= 0 ||
            Array.IndexOf(args, "--plugin-contract-e2e-checks") >= 0;

        RunManifestChecks();
        RunUiChecks();
        RunPathChecks();
        RunIpcChecks();
        RunTrustChecks();
        RunPackageFilesCheck();
        if (exampleEndToEnd) RunExampleBackendEndToEnd();
        Summary();
    }

    private static void Summary()
    {
        Console.WriteLine($"插件合同检查完成：{_checks} 项，失败 {_failures} 项。");
        if (_failures > 0) Environment.ExitCode = 1;
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        _checks++;
        if (condition) return;
        _failures++;
        Console.WriteLine($"  [失败] {name}{(detail is null ? "" : "：" + detail)}");
    }

    private static void CheckReject(string name, string json, Func<byte[], object> parse)
    {
        _checks++;
        try
        {
            parse(Encoding.UTF8.GetBytes(json));
        }
        catch (PluginContractException)
        {
            return;
        }
        _failures++;
        Console.WriteLine($"  [失败] {name}：应拒绝但未拒绝。");
    }

    /// <summary>字符串输入版本的拒绝断言（用于 ui/manifest 等以文本为源的解析器）。</summary>
    private static void CheckRejectText(string name, string json, Func<string, object> parse)
    {
        _checks++;
        try
        {
            parse(json);
        }
        catch (PluginContractException)
        {
            return;
        }
        _failures++;
        Console.WriteLine($"  [失败] {name}：应拒绝但未拒绝。");
    }

    private static void CheckRejectRaw(string name, Func<byte[], object> parse) => CheckReject(name, "placeholder", parse);

    // ---------- manifest ----------

    private const string ReportOperationJson =
        "{\"id\":\"report\",\"title\":\"生成报告\",\"mutating\":false,\"reversible\":false,\"targetScoped\":false,\"timeoutSeconds\":30}";

    private static string ManifestWithOperation(string operationJson) => $$"""
        {
          "schemaVersion": 1,
          "id": "org.example.hardware-report",
          "name": "硬件报告示例",
          "author": "Example Author",
          "version": "1.0.0",
          "protocolVersion": "1.0",
          "hostCompatibility": { "minInclusive": "0.83.0", "maxExclusive": "1.0.0" },
          "backend": { "entry": "backend/Example.Plugin.HardwareReport.exe", "architecture": "x64" },
          "permissions": [
            { "id": "hardware.read", "purpose": "生成硬件摘要" }
          ],
          "capabilities": { "continuous": false, "offlineAutonomous": false },
          "operations": [ OPERATION ]
        }
        """.Replace("OPERATION", operationJson);

    private const string ExampleManifest = """
        {
          "schemaVersion": 1,
          "id": "org.example.hardware-report",
          "name": "硬件报告示例",
          "author": "Example Author",
          "version": "1.0.0",
          "protocolVersion": "1.0",
          "hostCompatibility": { "minInclusive": "0.83.0", "maxExclusive": "1.0.0" },
          "backend": { "entry": "backend/Example.Plugin.HardwareReport.exe", "architecture": "x64" },
          "permissions": [
            { "id": "hardware.read", "purpose": "生成硬件摘要" }
          ],
          "capabilities": { "continuous": false, "offlineAutonomous": false },
          "operations": [
            { "id": "report", "title": "生成报告", "mutating": false,
              "reversible": false, "targetScoped": false, "timeoutSeconds": 30 }
          ]
        }
        """;

    private static void RunManifestChecks()
    {
        CheckReject("未知根字段", ExampleManifest.Replace("\"schemaVersion\": 1", "\"extra\":true,\"schemaVersion\": 1"), PluginManifestParser.Parse);
        foreach (var oldValue in new[] { "\"architecture\": \"x64\"", "\"purpose\": \"生成硬件摘要\"", "\"continuous\": false", "\"minInclusive\": \"0.83.0\"", "\"timeoutSeconds\": 30" })
            CheckReject("未知嵌套字段 " + oldValue, ExampleManifest.Replace(oldValue, oldValue + ",\"extra\":true"), PluginManifestParser.Parse);
        CheckReject("错误超时类型", ExampleManifest.Replace("\"timeoutSeconds\": 30", "\"timeoutSeconds\":\"bad\""), PluginManifestParser.Parse);
        CheckReject("错误资源类型", ExampleManifest.Replace("\"timeoutSeconds\": 30", "\"timeoutSeconds\":30,\"resourceIds\":null"), PluginManifestParser.Parse);
        Check("两段版本拒绝", !PluginManifestParser.TryParseVersion("1.0", out _));
        Check("前导零版本拒绝", !PluginManifestParser.TryParseVersion("01.0.0", out _));
        Console.WriteLine("== manifest.json 合同 ==");
        var manifest = PluginManifestParser.Parse(Encoding.UTF8.GetBytes(ExampleManifest));
        Check("解析示例 manifest", manifest.Id == "org.example.hardware-report" && manifest.Version == "1.0.0");
        Check("协议版本", manifest.ProtocolMajor == 1 && manifest.ProtocolMinor == 0);
        Check("宿主范围", manifest.HostCompatibility.MinInclusive.ToString() == "0.83.0" &&
            manifest.HostCompatibility.MaxExclusive.ToString() == "1.0.0");
        Check("宿主兼容判定", manifest.IsCompatible(new Version(0, 83, 0)) && !manifest.IsCompatible(new Version(1, 0, 0, 0)) &&
            !manifest.IsCompatible(new Version(0, 82, 9)));
        Check("权限解析", manifest.Permissions.Count == 1 && manifest.Permissions[0].Id == "hardware.read");
        Check("能力解析", !manifest.Capabilities.Continuous && !manifest.Capabilities.OfflineAutonomous);
        Check("操作解析", manifest.FindOperation("report") is { Mutating: false, TimeoutSeconds: 30 });
        Check("未知操作", !manifest.HasOperation("nope"));

        // 合法修改操作：必须 reversible + resourceIds + targetScoped。
        var mutating = PluginManifestParser.Parse(Encoding.UTF8.GetBytes(ManifestWithOperation(
            "{\"id\":\"tune\",\"title\":\"调优\",\"mutating\":true,\"reversible\":true,\"targetScoped\":true,\"timeoutSeconds\":60,\"resourceIds\":[\"cpu.sets\"]}")));
        Check("修改操作合法解析", mutating.FindOperation("tune") is { Mutating: true, Reversible: true } &&
            mutating.FindOperation("tune")!.ResourceIds.SequenceEqual(["cpu.sets"]));

        CheckReject("拒绝：schemaVersion 错误", Replace(ExampleManifest, "\"schemaVersion\": 1", "\"schemaVersion\": 2"), PluginManifestParser.Parse);
        CheckReject("拒绝：重复属性", ExampleManifest.Replace("\"author\"", "\"author\":\"A\",\"author\"", StringComparison.Ordinal), PluginManifestParser.Parse);
        CheckReject("拒绝：非法 id（大写）", Replace(ExampleManifest, "org.example.hardware-report", "Org.Example.Report"), PluginManifestParser.Parse);
        CheckReject("拒绝：id 伪装官方域", Replace(ExampleManifest, "org.example.hardware-report", "deltanfd.example.report"), PluginManifestParser.Parse);
        CheckReject("拒绝：id 单段", Replace(ExampleManifest, "org.example.hardware-report", "example"), PluginManifestParser.Parse);
        CheckReject("拒绝：版本带预发布", Replace(ExampleManifest, "\"version\": \"1.0.0\"", "\"version\": \"1.0.0-beta\""), PluginManifestParser.Parse);
        CheckReject("拒绝：版本带构建元数据", Replace(ExampleManifest, "\"version\": \"1.0.0\"", "\"version\": \"1.0.0+abc\""), PluginManifestParser.Parse);
        CheckReject("拒绝：协议主版本 2", Replace(ExampleManifest, "\"protocolVersion\": \"1.0\"", "\"protocolVersion\": \"2.0\""), PluginManifestParser.Parse);
        CheckReject("拒绝：协议次版本高于宿主", Replace(ExampleManifest, "\"protocolVersion\": \"1.0\"", "\"protocolVersion\": \"1.1\""), PluginManifestParser.Parse);
        CheckReject("拒绝：范围倒置", Replace(ExampleManifest, "\"minInclusive\": \"0.83.0\", \"maxExclusive\": \"1.0.0\"", "\"minInclusive\": \"1.0.0\", \"maxExclusive\": \"0.83.0\""), PluginManifestParser.Parse);
        CheckReject("拒绝：入口不在 backend 下", Replace(ExampleManifest, "backend/Example.Plugin.HardwareReport.exe", "app/Example.exe"), PluginManifestParser.Parse);
        CheckReject("拒绝：入口非 EXE", Replace(ExampleManifest, "backend/Example.Plugin.HardwareReport.exe", "backend/Example.dll"), PluginManifestParser.Parse);
        CheckReject("拒绝：未知权限类别", Replace(ExampleManifest, "\"hardware.read\"", "\"magic.power\""), PluginManifestParser.Parse);
        CheckReject("拒绝：权限缺用途", ExampleManifest.Replace("\"purpose\": \"生成硬件摘要\"", "", StringComparison.Ordinal), PluginManifestParser.Parse);
        CheckReject("拒绝：修改操作不可恢复", ManifestWithOperation(
            "{\"id\":\"tune\",\"title\":\"调优\",\"mutating\":true,\"reversible\":false,\"targetScoped\":false}"), PluginManifestParser.Parse);
        CheckReject("拒绝：修改操作缺 resourceIds", ManifestWithOperation(
            "{\"id\":\"tune\",\"title\":\"调优\",\"mutating\":true,\"reversible\":true,\"targetScoped\":false}"), PluginManifestParser.Parse);
        CheckReject("拒绝：未知 resourceIds", ManifestWithOperation(
            "{\"id\":\"tune\",\"title\":\"调优\",\"mutating\":true,\"reversible\":true,\"targetScoped\":false,\"resourceIds\":[\"gpu.overclock\"]}"), PluginManifestParser.Parse);
        CheckReject("拒绝：resourceIds 重复", ManifestWithOperation(
            "{\"id\":\"tune\",\"title\":\"调优\",\"mutating\":true,\"reversible\":true,\"targetScoped\":false,\"resourceIds\":[\"cpu.sets\",\"cpu.sets\"]}"), PluginManifestParser.Parse);
        CheckReject("拒绝：超时越界", ManifestWithOperation(
            "{\"id\":\"tune\",\"title\":\"调优\",\"mutating\":false,\"reversible\":false,\"targetScoped\":false,\"timeoutSeconds\":301}"), PluginManifestParser.Parse);
        CheckReject("拒绝：缺少 capabilities", ExampleManifest.Replace("\"capabilities\": { \"continuous\": false, \"offlineAutonomous\": false },", "", StringComparison.Ordinal), PluginManifestParser.Parse);
        CheckReject("拒绝：空 operations", ManifestWithOperation("{}"), PluginManifestParser.Parse);
        CheckReject("拒绝：操作 id 重复", ManifestWithOperation(
            "{\"id\":\"tune\",\"title\":\"调优\",\"mutating\":false,\"reversible\":false,\"targetScoped\":false},{\"id\":\"tune\",\"title\":\"调优2\",\"mutating\":false,\"reversible\":false,\"targetScoped\":false}"), PluginManifestParser.Parse);
        CheckReject("拒绝：坏 JSON", ExampleManifest[..^20], PluginManifestParser.Parse);
        CheckRejectRaw("拒绝：非法 UTF-8", _ => PluginManifestParser.Parse([0xFF, 0xFE, 0x7F]));
        CheckRejectRaw("拒绝：JSON 超 1MiB", _ => PluginManifestParser.Parse(new byte[PluginContract.MaxJsonBytes + 1]));
        CheckReject("拒绝：布尔类型不符", ExampleManifest.Replace("\"continuous\": false", "\"continuous\": \"false\"", StringComparison.Ordinal), PluginManifestParser.Parse);
        CheckReject("拒绝：尾部逗号", ExampleManifest.Replace(
            "\"capabilities\": { \"continuous\": false, \"offlineAutonomous\": false },", "\"capabilities\": { \"continuous\": false, \"offlineAutonomous\": false, },", StringComparison.Ordinal), PluginManifestParser.Parse);

        Console.WriteLine("  manifest 检查完成。");
    }

    // ---------- ui ----------

    private const string ExampleUi = """
        {
          "schemaVersion": 1,
          "controls": [
            { "id": "title", "type": "text", "text": "硬件报告" },
            { "id": "detail", "type": "toggle", "label": "详细信息", "default": false },
            { "id": "run", "type": "button", "label": "生成报告", "operationId": "report" },
            { "id": "progress", "type": "progress" },
            { "id": "result", "type": "result" }
          ]
        }
        """;

    private static void RunUiChecks()
    {
        Console.WriteLine("== ui.json 合同 ==");
        var manifest = PluginManifestParser.Parse(Encoding.UTF8.GetBytes(ExampleManifest));
        var ui = PluginUiParser.Parse(Encoding.UTF8.GetBytes(ExampleUi), manifest);
        Check("解析示例 ui", ui.Controls.Count == 5);
        var defaults = ui.DefaultValues();
        Check("默认值组装", defaults.TryGetValue("detail", out var detail) && detail is false && !defaults.ContainsKey("run"));
        Check("按钮绑定操作", ui.FindControl("run") is { Type: PluginControlType.Button, OperationId: "report" });

        var uiParse = (string json) => PluginUiParser.Parse(Encoding.UTF8.GetBytes(json), manifest);
        CheckRejectText("拒绝：未知控件类型", Replace(ExampleUi, "\"type\": \"toggle\"", "\"type\": \"canvas\""), uiParse);
        CheckRejectText("拒绝：控件 id 重复", Replace(ExampleUi, "\"id\": \"detail\"", "\"id\": \"title\""), uiParse);
        CheckRejectText("拒绝：按钮绑定未知操作", Replace(ExampleUi, "\"operationId\": \"report\"", "\"operationId\": \"hack\""), uiParse);
        CheckRejectText("拒绝：number 缺 step/default", Replace(ExampleUi,
            "\"id\": \"detail\", \"type\": \"toggle\", \"label\": \"详细信息\", \"default\": false",
            "\"id\": \"count\", \"type\": \"number\", \"label\": \"数量\", \"min\": 0, \"max\": 10"), uiParse);
        CheckRejectText("拒绝：number 默认值越界", Replace(ExampleUi,
            "\"id\": \"detail\", \"type\": \"toggle\", \"label\": \"详细信息\", \"default\": false",
            "\"id\": \"count\", \"type\": \"number\", \"label\": \"数量\", \"min\": 0, \"max\": 10, \"step\": 1, \"default\": 11"), uiParse);
        CheckRejectText("拒绝：number step 非正", Replace(ExampleUi,
            "\"id\": \"detail\", \"type\": \"toggle\", \"label\": \"详细信息\", \"default\": false",
            "\"id\": \"count\", \"type\": \"number\", \"label\": \"数量\", \"min\": 0, \"max\": 10, \"step\": 0, \"default\": 5"), uiParse);
        CheckRejectText("拒绝：select 默认值未声明", Replace(ExampleUi,
            "\"id\": \"detail\", \"type\": \"toggle\", \"label\": \"详细信息\", \"default\": false",
            "\"id\": \"mode\", \"type\": \"select\", \"label\": \"模式\", \"options\": [{ \"value\": \"a\", \"label\": \"A\" }], \"default\": \"b\""), uiParse);
        CheckRejectText("拒绝：select 选项 value 重复", Replace(ExampleUi,
            "\"id\": \"detail\", \"type\": \"toggle\", \"label\": \"详细信息\", \"default\": false",
            "\"id\": \"mode\", \"type\": \"select\", \"label\": \"模式\", \"options\": [{ \"value\": \"a\", \"label\": \"A\" }, { \"value\": \"a\", \"label\": \"B\" }], \"default\": \"a\""), uiParse);
        CheckRejectText("拒绝：未知字段（脚本注入）", Replace(ExampleUi, "\"id\": \"progress\", \"type\": \"progress\"",
            "\"id\": \"progress\", \"type\": \"progress\", \"script\": \"run()\""), uiParse);
        CheckRejectText("拒绝：controls 为空",
            "{\"schemaVersion\":1,\"controls\":[]}", uiParse);

        Check("输入校验：合法", PluginUiParser.TryValidateValues(ui, new Dictionary<string, object?> { ["detail"] = true }, out _));
        Check("输入校验：未知控件", !PluginUiParser.TryValidateValues(ui, new Dictionary<string, object?> { ["nope"] = true }, out _));
        Check("输入校验：类型不符", !PluginUiParser.TryValidateValues(ui, new Dictionary<string, object?> { ["detail"] = "yes" }, out _));
        Check("输入校验：非输入控件", !PluginUiParser.TryValidateValues(ui, new Dictionary<string, object?> { ["run"] = true }, out _));
        var intUi = uiParse(Replace(ExampleUi,
            "\"id\": \"detail\", \"type\": \"toggle\", \"label\": \"详细信息\", \"default\": false",
            "\"id\": \"count\", \"type\": \"number\", \"label\": \"数量\", \"min\": 0, \"max\": 10, \"step\": 1, \"default\": 5"));
        Check("输入校验：number 越界", !PluginUiParser.TryValidateValues(intUi, new Dictionary<string, object?> { ["count"] = 11d }, out _));
        Check("输入校验：number 合法", PluginUiParser.TryValidateValues(intUi, new Dictionary<string, object?> { ["count"] = 7d }, out _));

        Console.WriteLine("  ui 检查完成。");
    }

    // ---------- 路径 ----------

    private static void RunPathChecks()
    {
        Console.WriteLine("== 包内路径校验 ==");
        Check("合法路径", PluginPath.ValidateRelativePath("backend/deps/lib.dll", "测试") == "backend/deps/lib.dll");
        Check("分隔符归一", PluginPath.IsSafeRelativePath("backend\\Example.exe"));
        var reject = (string name, string path) => CheckRejectRaw(name, _ => PluginPath.ValidateRelativePath(path, "测试"));
        reject("拒绝：绝对路径", "/etc/passwd");
        reject("拒绝：盘符", "C:/windows/system32");
        reject("拒绝：UNC", "\\\\server\\share");
        reject("拒绝：.. 段", "backend/../../escape");
        reject("拒绝：. 段", "backend/./x");
        reject("拒绝：冒号（备用数据流）", "backend/file.txt:stream");
        reject("拒绝：设备名", "backend/CON");
        reject("拒绝：设备名（带扩展）", "backend/con.exe");
        reject("拒绝：段尾空格", "backend/dir /file.exe");
        reject("拒绝：尾部点", "backend/file.");
        reject("拒绝：空段", "backend//x");
        reject("拒绝：非法字符", "backend/q?.exe");
        reject("拒绝：设备名大小写折叠", "backend/CoN.exe");
        CheckRejectRaw("拒绝：NUL 字符", _ => PluginPath.NormalizeSeparators("back\0end"));
        CheckRejectRaw("拒绝：多行", _ => PluginPath.ValidateRelativePath("a\nb", "测试"));

        Console.WriteLine("  路径检查完成。");
    }

    // ---------- IPC ----------

    /// <summary>构造完整 envelope JSON（普通字符串拼接，避免内插原始字符串的花括号歧义）。</summary>
    private static string EnvelopeJson(string type, Guid sessionId, Guid requestId, string payloadJson) =>
        "{\"protocol\":\"1.0\",\"type\":\"" + type + "\",\"sessionId\":\"" + sessionId.ToString("D") +
        "\",\"requestId\":\"" + requestId.ToString("D") + "\",\"payload\":" + payloadJson + "}";

    private static byte[] EncodeEnvelope(string type, Guid sessionId, Guid requestId, string payloadJson) =>
        PluginIpc.EncodeFrame(EnvelopeJson(type, sessionId, requestId, payloadJson));

    /// <summary>解析一帧并返回其 payload（帧本身非法时抛 PluginContractException）。</summary>
    private static JsonElement ParseEnvelopePayload(byte[] framed)
    {
        if (!PluginIpc.ParseFrame(framed, out var frame)) throw new PluginContractException("测试帧解析失败。");
        return frame.Payload;
    }

    private static void RunIpcChecks()
    {
        Console.WriteLine("== IPC 1.0 帧合同 ==");
        var sessionId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var nonce = Convert.ToBase64String(new byte[32]);

        // 合法 invoke 帧：编码 → 流式解析（半帧、多帧、粘包）
        var invokeFrame = EncodeEnvelope("invoke", sessionId, requestId,
            "{\"operationId\":\"report\",\"values\":{\"detail\":true},\"target\":null}");
        Check("帧编码含长度前缀", BinaryPrimitives.ReadUInt32LittleEndian(invokeFrame.AsSpan(0, 4)) == invokeFrame.Length - 4);

        var pingFrame = PluginIpc.Encode(PluginIpcType.Ping, sessionId, Guid.NewGuid(), "{}");
        var concatenated = new byte[invokeFrame.Length * 2 + pingFrame.Length];
        invokeFrame.CopyTo(concatenated, 0);
        invokeFrame.CopyTo(concatenated, invokeFrame.Length);
        pingFrame.CopyTo(concatenated, invokeFrame.Length * 2);
        var sequence = new ReadOnlySequence<byte>(concatenated);
        var parsed = 0;
        PluginIpcFrame? last = null;
        while (PluginIpc.TryReadFrame(ref sequence, out var frame)) { parsed++; last = frame; }
        Check("粘包/多帧解析", parsed == 3 && last!.Type == PluginIpcType.Ping);
        var partialBytes = new byte[invokeFrame.Length - 3];
        invokeFrame.AsSpan(0, partialBytes.Length).CopyTo(partialBytes);
        var partial = new ReadOnlySequence<byte>(partialBytes);
        Check("半帧不消费", !PluginIpc.TryReadFrame(ref partial, out _));
        Check("半帧不推进", partial.Length == partialBytes.Length);

        Check("ping 解析", last!.Type == PluginIpcType.Ping && last.SessionId == sessionId);
        Check("invoke 帧解析", PluginIpc.ParseFrame(invokeFrame, out var invoke));
        CheckRejectRaw("单帧零长度头", _ => { var bad = invokeFrame.ToArray(); Array.Clear(bad, 0, 4); return ParseEnvelopePayload(bad); });
        CheckRejectRaw("单帧截断", _ => ParseEnvelopePayload(invokeFrame[..^1]));
        CheckRejectRaw("单帧额外字节", _ => ParseEnvelopePayload(invokeFrame.Concat(new byte[] { 0 }).ToArray()));

        var manifest = PluginManifestParser.Parse(Encoding.UTF8.GetBytes(ExampleManifest));
        var ui = PluginUiParser.Parse(Encoding.UTF8.GetBytes(ExampleUi), manifest);
        var (operationId, values, target) = PluginIpc.ParseInvoke(invoke!.Payload, manifest, ui);
        Check("invoke payload", operationId == "report" && values["detail"] is true && target is null);

        // 非法帧
        CheckRejectRaw("拒绝：帧长为 0", _ => { var s = new ReadOnlySequence<byte>([0, 0, 0, 0]); PluginIpc.TryReadFrame(ref s, out PluginIpcFrame? _); return 0; });
        CheckRejectRaw("拒绝：帧长超限", _ => { var s = new ReadOnlySequence<byte>([0x01, 0x00, 0x10, 0x00]); PluginIpc.TryReadFrame(ref s, out PluginIpcFrame? _); return 0; });
        CheckRejectRaw("拒绝：协议版本不符",
            _ => ParseEnvelopePayload(PluginIpc.EncodeFrame(EnvelopeJson2("2.0", sessionId, requestId, "{}"))));
        CheckRejectRaw("拒绝：未知消息类型",
            _ => ParseEnvelopePayload(PluginIpc.EncodeFrame(EnvelopeJson2("1.0", sessionId, requestId, "{}").Replace("\"type\":\"ping\"", "\"type\":\"exploit\"", StringComparison.Ordinal))));
        CheckRejectRaw("拒绝：sessionId 非 UUID",
            _ => ParseEnvelopePayload(PluginIpc.EncodeFrame(EnvelopeJson("ping", sessionId, requestId, "{}").Replace(sessionId.ToString("D"), "not-a-uuid", StringComparison.Ordinal))));
        CheckRejectRaw("拒绝：未知 envelope 字段",
            _ => ParseEnvelopePayload(PluginIpc.EncodeFrame(EnvelopeJson("ping", sessionId, requestId, "{}").Replace(",\"payload\":{}", ",\"extra\":1,\"payload\":{}", StringComparison.Ordinal))));
        CheckRejectRaw("拒绝：payload 非对象",
            _ => ParseEnvelopePayload(PluginIpc.EncodeFrame(EnvelopeJson("ping", sessionId, requestId, "[1]"))));

        // hello / ready
        Check("hello 解析", PluginIpc.ParseHello(ParseEnvelopePayload(EncodeEnvelope("hello", sessionId, requestId,
            "{\"nonce\":\"" + nonce + "\",\"hostVersion\":\"0.83.0\",\"dataDirectory\":\"C:\\\\ProgramData\"}"))).HostVersion.ToString() == "0.83.0");
        CheckRejectRaw("拒绝：hello nonce 非 32 字节",
            _ => PluginIpc.ParseHello(ParseEnvelopePayload(EncodeEnvelope("hello", sessionId, requestId,
                "{\"nonce\":\"AAAA\",\"hostVersion\":\"0.83.0\",\"dataDirectory\":\"C:\\\\x\"}"))));
        Check("ready 解析", PluginIpc.ParseReady(ParseEnvelopePayload(EncodeEnvelope("ready", sessionId, requestId,
            "{\"nonce\":\"" + nonce + "\",\"pluginId\":\"org.example.hardware-report\",\"pluginVersion\":\"1.0.0\",\"protocol\":\"1.0\",\"pid\":1234}"))).Pid == 1234);
        CheckRejectRaw("拒绝：ready 协议 1.1",
            _ => PluginIpc.ParseReady(ParseEnvelopePayload(EncodeEnvelope("ready", sessionId, requestId,
                "{\"nonce\":\"" + nonce + "\",\"pluginId\":\"org.example.hardware-report\",\"pluginVersion\":\"1.0.0\",\"protocol\":\"1.1\",\"pid\":1234}"))));

        // result
        var resultPayload = "{\"status\":\"success\",\"code\":\"OK\",\"message\":\"报告已生成\",\"items\":[{\"id\":\"report\",\"status\":\"success\",\"message\":\"仅查询\"}],\"pendingRestore\":false,\"backupIds\":[]}";
        Check("result 解析", PluginIpc.ParseResult(ParseEnvelopePayload(EncodeEnvelope("result", sessionId, requestId, resultPayload)))
            is { Status: "success", PendingRestore: false, Items.Count: 1 });
        foreach (var badStatus in new[] { "\"ok\"", "\"SUCCESS\"" })
            CheckRejectRaw($"拒绝：result status {badStatus}",
                _ => PluginIpc.ParseResult(ParseEnvelopePayload(EncodeEnvelope("result", sessionId, requestId,
                    resultPayload.Replace("\"status\":\"success\",\"code\"", badStatus + ",\"code\"", StringComparison.Ordinal)))));
        CheckRejectRaw("拒绝：result 缺 pendingRestore",
            _ => PluginIpc.ParseResult(ParseEnvelopePayload(EncodeEnvelope("result", sessionId, requestId,
                resultPayload.Replace(",\"pendingRestore\":false", "", StringComparison.Ordinal)))));

        // target 快照：startTimeUtcTicks 必须十进制字符串
        var scopedManifest = PluginManifestParser.Parse(Encoding.UTF8.GetBytes(
            ExampleManifest.Replace("\"targetScoped\": false", "\"targetScoped\": true", StringComparison.Ordinal)));
        CheckRejectRaw("拒绝：targetScoped 缺 target",
            _ => PluginIpc.ParseInvoke(ParseEnvelopePayload(EncodeEnvelope("invoke", sessionId, requestId,
                "{\"operationId\":\"report\",\"values\":{},\"target\":null}")), scopedManifest, ui));
        var targetPayload = "{\"operationId\":\"report\",\"values\":{},\"target\":{\"generation\":7,\"executablePath\":\"C:\\\\Games\\\\df.exe\",\"pid\":4242,\"startTimeUtcTicks\":\"638000000000000000\"}}";
        Check("target 十进制字符串 ticks", PluginIpc.ParseInvoke(ParseEnvelopePayload(
            EncodeEnvelope("invoke", sessionId, requestId, targetPayload)), scopedManifest, ui).Target
            is { Generation: 7, Pid: 4242, StartTimeUtcTicks: 638000000000000000 });
        CheckRejectRaw("拒绝：target ticks 为 JSON 数字",
            _ => PluginIpc.ParseInvoke(ParseEnvelopePayload(EncodeEnvelope("invoke", sessionId, requestId,
                targetPayload.Replace("\"638000000000000000\"", "638000000000000000", StringComparison.Ordinal))), scopedManifest, ui));
        CheckRejectRaw("拒绝：未知操作",
            _ => PluginIpc.ParseInvoke(ParseEnvelopePayload(EncodeEnvelope("invoke", sessionId, requestId,
                "{\"operationId\":\"hack\",\"values\":{},\"target\":null}")), manifest, ui));

        // cancel / restore / offline.runId / progress
        Check("cancel 解析", PluginIpc.ParseCancel(ParseEnvelopePayload(EncodeEnvelope("cancel", sessionId, requestId,
            "{\"referencedRequestId\":\"" + requestId.ToString("D") + "\"}"))) == requestId);
        CheckRejectRaw("拒绝：cancel 非 UUID",
            _ => PluginIpc.ParseCancel(ParseEnvelopePayload(EncodeEnvelope("cancel", sessionId, requestId,
                "{\"referencedRequestId\":\"x\"}"))));
        Check("restore 解析", PluginIpc.ParseRestore(ParseEnvelopePayload(EncodeEnvelope("restore", sessionId, requestId,
            "{\"backupIds\":[\"b1\",\"b2\"]}"))).Count == 2);
        Check("offline.runId 解析", PluginIpc.ParseRunId(ParseEnvelopePayload(EncodeEnvelope("offline.prepare", sessionId, requestId,
            "{\"runId\":\"" + sessionId.ToString("D") + "\"}")), "offline.prepare") == sessionId);
        var offlinePayload = "{\"runId\":\"" + sessionId + "\",\"target\":{\"generation\":7,\"executablePath\":\"C:\\\\Games\\\\df.exe\",\"pid\":4242,\"startTimeUtcTicks\":\"638000000000000000\"}}";
        Check("offline.prepare 目标快照", PluginIpc.ParseOfflinePrepare(ParseEnvelopePayload(
            EncodeEnvelope("offline.prepare", sessionId, requestId, offlinePayload))).Target?.Pid == 4242);
        CheckRejectRaw("offline.commit 不接受 target", _ => PluginIpc.ParseRunId(ParseEnvelopePayload(
            EncodeEnvelope("offline.commit", sessionId, requestId, offlinePayload)), "offline.commit"));
        CheckRejectRaw("拒绝：runId 空 UUID",
            _ => PluginIpc.ParseRunId(ParseEnvelopePayload(EncodeEnvelope("offline.commit", sessionId, requestId,
                "{\"runId\":\"00000000-0000-0000-0000-000000000000\"}")), "offline.commit"));
        var progress = PluginIpc.ParseProgress(ParseEnvelopePayload(EncodeEnvelope("progress", sessionId, requestId,
            "{\"percent\":50,\"message\":\"处理中\"}")));
        Check("progress 解析", progress.Percent == 50 && progress.Message == "处理中");
        Check("progress percent 可为 null", PluginIpc.ParseProgress(ParseEnvelopePayload(EncodeEnvelope("progress", sessionId, requestId,
            "{\"percent\":null,\"message\":\"x\"}"))).Percent is null);
        CheckRejectRaw("拒绝：progress 超界",
            _ => PluginIpc.ParseProgress(ParseEnvelopePayload(EncodeEnvelope("progress", sessionId, requestId,
                "{\"percent\":101,\"message\":\"x\"}"))));
        CheckRejectRaw("拒绝：progress percent 非整数",
            _ => PluginIpc.ParseProgress(ParseEnvelopePayload(EncodeEnvelope("progress", sessionId, requestId,
                "{\"percent\":1.5,\"message\":\"x\"}"))));

        Console.WriteLine("  IPC 检查完成。");
    }

    private static string EnvelopeJson2(string protocol, Guid sessionId, Guid requestId, string payloadJson) =>
        "{\"protocol\":\"" + protocol + "\",\"type\":\"ping\",\"sessionId\":\"" + sessionId.ToString("D") +
        "\",\"requestId\":\"" + requestId.ToString("D") + "\",\"payload\":" + payloadJson + "}";

    // ---------- 信任文案 ----------

    private static void RunTrustChecks()
    {
        Console.WriteLine("== 信任提示 ==");
        var manifest = PluginManifestParser.Parse(Encoding.UTF8.GetBytes(ExampleManifest));
        var checklist = PluginTrust.AuthorizationChecklist(manifest, new string('a', 64), signed: false);
        Check("确认清单包含作者", checklist.Any(l => l.Contains("作者")));
        Check("确认清单包含包哈希", checklist.Any(l => l.Contains("SHA256")));
        Check("确认清单包含管理员风险", checklist.Any(l => l.Contains("管理员")));
        Check("确认清单包含权限用途", checklist.Any(l => l.Contains("hardware.read")));
        Check("确认清单含沙箱免责", checklist.Any(l => l.Contains("不是权限沙箱")));
        Check("确认清单不承诺零风险", checklist.All(l =>
            !l.Contains("零风险") && (!l.Contains("零封号风险") || l.Contains("不能"))));
        Check("未签名标记来源未验证", checklist.Any(l => l.Contains("来源未验证")));
        Check("签名文案区分", PluginTrust.AuthorizationChecklist(manifest, new string('b', 64), signed: true)
            .Any(l => l.Contains("不等于安全认证")));
        var notice = PluginTrust.TrustBoundaryNotice();
        Check("边界说明含免责", notice.Contains("不是权限沙箱") && notice.Contains("不代表反作弊厂商认可"));
        Check("脱机授权含非零进程标记", PluginTrust.OfflineAuthorizationNotice(manifest).Contains("非零进程"));
        Console.WriteLine("  信任提示检查完成。");
    }

    // ---------- 示例包文件 ----------

    private static void RunPackageFilesCheck()
    {
        Console.WriteLine("== 示例插件包文件 ==");
        var root = FindRepoRoot();
        var packageDir = Path.Combine(root, "src", "Example.Plugin.HardwareReport", "package");
        var manifest = PluginManifestParser.Parse(File.ReadAllBytes(Path.Combine(packageDir, "manifest.json")));
        var ui = PluginUiParser.Parse(File.ReadAllBytes(Path.Combine(packageDir, "ui.json")), manifest);
        Check("仓库内 manifest.json 与规范示例一致", manifest.Id == "org.example.hardware-report" && manifest.Operations.Count == 1);
        Check("仓库内 ui.json 可解析", ui.Controls.Count == 5);
        foreach (var name in new[] { "README.md", "LICENSE.txt" })
            Check($"包含 {name}", File.Exists(Path.Combine(packageDir, name)));
        var readme = File.ReadAllText(Path.Combine(packageDir, "README.md"));
        Check("README 含作者/风险/恢复说明",
            readme.Contains("作者") && readme.Contains("风险") && readme.Contains("恢复") && readme.Contains("脱机"));
        Check("README 不宣传反作弊零风险", !readme.Contains("零封号") && !readme.Contains("反作弊认可"));

        var program = File.ReadAllText(Path.Combine(root, "src", "Example.Plugin.HardwareReport", "Program.cs"));
        Check("示例不引用宿主内部服务", !program.Contains("DeltaNFD.Services") && !program.Contains("using DeltaNFD"));
        Check("示例不请求持续/脱机能力", !manifest.Capabilities.Continuous && !manifest.Capabilities.OfflineAutonomous);
        Console.WriteLine("  示例包检查完成。");
    }

    // ---------- 示例后端端到端 ----------

    private static void RunExampleBackendEndToEnd()
    {
        Console.WriteLine("== 示例插件后端真实端到端 ==");
        var root = FindRepoRoot();
        var exe = FindExampleBackend(root);
        if (exe is null)
        {
            Console.WriteLine("  [失败] 未找到示例后端产物（先构建 src/Example.Plugin.HardwareReport）。");
            _failures++;
            return;
        }

        var pipeName = "DeltaNFD_PluginCheck_" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var session = Guid.NewGuid();
        start.ArgumentList.Add("--dnfd-pipe");
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add("--dnfd-session");
        start.ArgumentList.Add(session.ToString("D"));
        start.ArgumentList.Add("--dnfd-host-pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.RedirectStandardError = true;
        using var process = System.Diagnostics.Process.Start(start)!;
        var dataDirectory = Path.Combine(Path.GetTempPath(), "DeltaNFD_PluginCheck_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dataDirectory);
            using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    try { server.WaitForConnectionAsync(connectCts.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException)
                    {
                        if (process.HasExited) throw new IOException("示例后端连接前退出：" + process.ExitCode + " " + process.StandardError.ReadToEnd());
                        throw;
                    }
                }
            _checks++;
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var clientPid) || clientPid != process.Id)
            {
                _failures++;
                Console.WriteLine("  [失败] 管道客户端不是本次启动的示例后端。");
                return;
            }

            var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
            WriteServerFrame(server, "hello", session,
                "{\"nonce\":\"" + nonce + "\",\"hostVersion\":\"0.83.0\",\"dataDirectory\":\"" +
                dataDirectory.Replace("\\", "\\\\") + "\"}");
            if (process.HasExited)
            {
                _failures++;
                Console.WriteLine($"  [失败] 示例后端提前退出，退出码 {process.ExitCode}：{process.StandardError.ReadToEnd()}");
                return;
            }
            var ready = ReadServerFrame(server);
            Check("ready 帧类型", ready.Type == PluginIpcType.Ready);
            Check("ready 会话与请求", ready.SessionId == session && ready.RequestId == _lastRequestId);
            var readyPayload = PluginIpc.ParseReady(ready.Payload);
            Check("ready 回显 nonce", readyPayload.Nonce == nonce);
            Check("ready 身份", readyPayload.PluginId == "org.example.hardware-report" &&
                readyPayload.PluginVersion == "1.0.0" && readyPayload.Pid == process.Id);

            WriteServerFrame(server, "invoke", session,
                "{\"operationId\":\"report\",\"values\":{\"detail\":true},\"target\":null}");
            var result = ReadServerFrame(server);
            Check("result 帧类型", result.Type == PluginIpcType.Result);
            Check("result 回显 requestId", result.RequestId == _lastRequestId);
            var parsed = PluginIpc.ParseResult(result.Payload);
            Check("report 成功", parsed.Status == "success" && parsed.Code == "OK" && parsed.PendingRestore == false);
            Check("报告内容只读", parsed.Items.Count == 1 && parsed.Message.Contains("未修改系统") &&
                parsed.Items[0].Id == "report" && parsed.Items[0].Status == "success");

            WriteServerFrame(server, "ping", session, "{}");
            var pong = ReadServerFrame(server);
            Check("pong 心跳", pong.Type == PluginIpcType.Pong);
            Check("pong 请求关联", pong.RequestId == _lastRequestId && pong.SessionId == session);
            WriteServerFrame(server, "invoke", session, "{\"operationId\":\"report\",\"values\":{\"unknown\":true},\"target\":null}");
            Check("示例拒绝未知输入", PluginIpc.ParseResult(ReadServerFrame(server).Payload).Code == "PROTOCOL_ERROR");

            WriteServerFrame(server, "invoke", session,
                "{\"operationId\":\"unknown\",\"values\":{},\"target\":null}");
            var rejected = ReadServerFrame(server);
            Check("未知操作被拒", PluginIpc.ParseResult(rejected.Payload).Code == "UNAUTHORIZED" &&
                rejected.RequestId == _lastRequestId);

            WriteServerFrame(server, "stop", session, "{}");
            var stopped = ReadServerFrame(server);
            Check("stop 确认", PluginIpc.ParseResult(stopped.Payload).Code == "OK");
            process.WaitForExit(10_000);
            Check("进程退出", process.HasExited && process.ExitCode == 0);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
            server.Dispose();
            try { Directory.Delete(dataDirectory, recursive: true); } catch (IOException) { }
        }
        Console.WriteLine("  示例后端端到端完成。");
        foreach (var scenario in new[] { "silent", "hello-session", "message-session", "duplicate" })
            RunExampleRejection(exe, scenario);
    }

    private static void RunExampleRejection(string exe, string scenario)
    {
        var name = "DeltaNFD_PluginReject_" + Guid.NewGuid().ToString("N");
        var session = Guid.NewGuid();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--dnfd-pipe", name, "--dnfd-session", session.ToString("D"), "--dnfd-host-pid", Environment.ProcessId.ToString() })
            start.ArgumentList.Add(arg);
        using var child = System.Diagnostics.Process.Start(start)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            server.WaitForConnectionAsync(deadline.Token).GetAwaiter().GetResult();
            if (scenario != "silent")
            {
                var payload = JsonSerializer.Serialize(new { nonce = Convert.ToBase64String(new byte[32]), hostVersion = "0.83.0", dataDirectory = Path.GetTempPath() });
                if (scenario == "duplicate") payload = payload.Replace("\"hostVersion\":", "\"nonce\":\"bad\",\"hostVersion\":");
                WriteServerFrame(server, "hello", scenario == "hello-session" ? Guid.NewGuid() : session, payload);
                if (scenario == "message-session")
                {
                    ReadServerFrame(server);
                    WriteServerFrame(server, "invoke", Guid.NewGuid(), "{\"operationId\":\"report\",\"values\":{},\"target\":null}");
                }
            }
            child.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
            Check("后端拒绝 " + scenario, child.ExitCode != 0);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); }
        }
    }

    private static string? FindExampleBackend(string root)
    {
        var projectDir = Path.Combine(root, "src", "Example.Plugin.HardwareReport");
        // 有 Platform 属性时 MSBuild 走 bin/<Platform>/<Config>，否则 bin/<Config>；两种布局都找。
        foreach (var configuration in new[] { "Debug", "Release" })
        foreach (var platformSegment in new[] { "x64", "" })
        {
            var candidate = Path.Combine(projectDir, "bin", platformSegment, configuration, "net8.0-windows",
                "Example.Plugin.HardwareReport.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static Guid _lastRequestId;

    private static void WriteServerFrame(NamedPipeServerStream pipe, string type, Guid session, string payloadJson)
    {
        _lastRequestId = Guid.NewGuid();
        var frame = PluginIpc.EncodeFrame(EnvelopeJson(type, session, _lastRequestId, payloadJson));
        pipe.Write(frame, 0, frame.Length);
        pipe.Flush();
    }

    private static PluginIpcFrame ReadServerFrame(NamedPipeServerStream pipe)
    {
        var header = new byte[4];
        ReadExactly(pipe, header);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > PluginContract.MaxFrameBytes) throw new IOException("帧长度非法。");
        var payload = new byte[length];
        ReadExactly(pipe, payload);
        var full = new byte[4 + length];
        header.CopyTo(full, 0);
        payload.CopyTo(full, 4);
        if (!PluginIpc.ParseFrame(full, out var frame)) throw new IOException("帧解析失败。");
        return frame;
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        stream.ReadExactlyAsync(buffer, deadline.Token).AsTask().GetAwaiter().GetResult();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeltaNFD.sln")))
            directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("未找到仓库根目录。");
        return directory.FullName;
    }

    private static string Replace(string text, string oldText, string newText)
    {
        if (!text.Contains(oldText, StringComparison.Ordinal))
            throw new InvalidOperationException("测试夹具损坏：待替换文本不存在：" + oldText);
        return text.Replace(oldText, newText, StringComparison.Ordinal);
    }
}
