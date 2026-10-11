using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeltaNFD.Services;

internal sealed record DotNetInstaller(Version Version, Uri Url, string Sha512);

/// <summary>优先使用微软 .NET 8 x64 离线包，缺包才下载；校验后打开官方交互安装器。</summary>
internal static class DotNetRepairService
{
    private const string MetadataUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json";
    private const long MaxInstallerBytes = 150 * 1024 * 1024;
    private static int _busy;
    internal static bool IsBusy => Volatile.Read(ref _busy) != 0;
    internal static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static DotNetInstaller SelectInstaller(string json)
    {
        using var metadata = JsonDocument.Parse(json);
        var available = new List<DotNetInstaller>();
        foreach (var release in metadata.RootElement.GetProperty("releases").EnumerateArray())
        {
            if (!release.TryGetProperty("runtime", out var runtime)
                || !Version.TryParse(runtime.GetProperty("version").GetString(), out var version)
                || version.Major != 8 || version.Minor != 0) continue;
            foreach (var file in runtime.GetProperty("files").EnumerateArray())
            {
                if (file.GetProperty("rid").GetString() != "win-x64" || file.GetProperty("name").GetString() != "dotnet-runtime-win-x64.exe") continue;
                var uri = new Uri(file.GetProperty("url").GetString()!, UriKind.Absolute);
                var hash = file.GetProperty("hash").GetString() ?? "";
                if (!AllowedDownload(uri) || hash.Length != 128 || !hash.All(Uri.IsHexDigit))
                    throw new InvalidDataException("微软 .NET 安装器地址或 SHA512 无法确认。");
                available.Add(new(version, uri, hash));
            }
        }
        return available.OrderByDescending(p => p.Version).FirstOrDefault()
            ?? throw new InvalidDataException("未找到 .NET 8 x64 正式版 Runtime 安装器。");
    }

    internal static bool AllowedDownload(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && (uri.Host.Equals("builds.dotnet.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("download.visualstudio.microsoft.com", StringComparison.OrdinalIgnoreCase));

    internal static bool VerifyHash(Stream stream, string expected)
    {
        stream.Position = 0;
        var actual = Convert.ToHexString(SHA512.HashData(stream));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static DotNetInstaller BundledInstaller => new(new Version(BundledDotNetRuntime.Version),
        new Uri(BundledDotNetRuntime.Url), BundledDotNetRuntime.Sha512);

    internal static string? FindBundledInstaller(string baseDirectory)
    {
        var folder = Path.Combine(baseDirectory, "Assets", "DotNetRepair");
        var path = Path.Combine(folder, BundledDotNetRuntime.FileName);
        if (!File.Exists(path)) return null;
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("离线 .NET 安装器路径含重解析点，已拒绝启动。");
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length <= 0 || info.Length > MaxInstallerBytes)
            throw new InvalidDataException("离线 .NET 安装器大小或路径异常，已拒绝启动。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!VerifyHash(stream, BundledDotNetRuntime.Sha512))
            throw new InvalidDataException("离线 .NET 安装器 SHA512 校验失败，请重新安装工具；已拒绝启动。");
        return path;
    }

    internal static string ExitMessage(int code) => code switch
    {
        0 => "微软安装器已完成，将重新检测。",
        3010 or 1641 => "微软安装器要求重启；请保存工作并手动重启，再重新检测。",
        1602 or 1223 => "已取消 .NET 安装/修复。",
        1618 => "另一个安装任务正在运行，请完成后重试。",
        1638 => "安装器报告版本冲突，不能视为安装成功。请查看诊断日志。",
        _ => $"微软安装器未成功完成（退出码 {code}），请查看诊断日志。",
    };

    internal static async Task<string> RepairAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return "已有 .NET 修复任务正在运行。";
        try
        {
            // Participate in exit/deployment handover; a handover already in progress rejects this task.
            using var operation = Backends.BackendOperationGate.EnterStart();
            var before = await DotNetEnvironmentService.CheckAsync(cancellationToken);
            if (before.NeedsAppRepair)
                return "工具自带的 .NET 文件或配置缺损。系统 .NET 安装器无法替换这些文件，请使用本工具安装包重新安装后再检测。\n" + before.Details;
            if (before.Components.All(c => c.Bundled))
                return "本工具各组件均自带 .NET，当前文件检查通过，无需另装系统 .NET。";

            using var downloadBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadBudget.CancelAfter(TimeSpan.FromMinutes(10));
            var token = downloadBudget.Token;
            progress.Report("正在核验随工具携带的 .NET 8 x64 离线包…");
            var bundledPath = await Task.Run(() => FindBundledInstaller(AppContext.BaseDirectory), token);
            DotNetInstaller installer;
            if (bundledPath is not null) installer = BundledInstaller;
            else
            {
                progress.Report("没有离线包，正在获取微软 .NET 8 x64 正式版安装信息…");
                using var metadataResponse = await Client.GetAsync(MetadataUrl, HttpCompletionOption.ResponseHeadersRead, token);
                metadataResponse.EnsureSuccessStatusCode();
                var metadataBytes = await ReadBoundedAsync(metadataResponse.Content, 8 * 1024 * 1024, token);
                installer = SelectInstaller(System.Text.Encoding.UTF8.GetString(metadataBytes));
            }
            var folder = Path.Combine(AppDataPaths.Root, "dotnet-repair", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"dotnet-runtime-{installer.Version}-win-x64.exe");
            var log = Path.Combine(folder, "install.log");
            if (bundledPath is not null)
            {
                progress.Report($"正在准备离线 .NET Runtime {installer.Version}（x64），无需联网…");
                await using var source = new FileStream(bundledPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await source.CopyToAsync(destination, token);
            }
            else
            {
                progress.Report($"正在下载 .NET Runtime {installer.Version}（x64）…");
                using var response = await Client.GetAsync(installer.Url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxInstallerBytes) throw new InvalidDataException("安装器大小超限。");
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920]; long total = 0, reported = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) != 0)
                {
                    total += read;
                    if (total > MaxInstallerBytes) throw new InvalidDataException("安装器大小超限。");
                    await destination.WriteAsync(buffer.AsMemory(0, read), token);
                    if (total - reported > 1024 * 1024) { reported = total; progress.Report($"正在下载 .NET 8 x64：{total / 1024 / 1024} MB…"); }
                }
                await destination.FlushAsync(token);
            }
            progress.Report("正在核验 SHA512…");
            using var verified = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!await Task.Run(() => VerifyHash(verified, installer.Sha512), token)) throw new InvalidDataException("安装器 SHA512 校验失败，已拒绝启动。");
            token.ThrowIfCancellationRequested();
            // /repair is appropriate only when this exact runtime version is installed.
            var exact = DotNetEnvironmentService.SystemRoots().Any(root =>
                Directory.Exists(Path.Combine(root, "shared", "Microsoft.NETCore.App", installer.Version.ToString())));
            progress.Report("已打开微软安装器。请在安装器中完成或取消；此阶段不能通过本工具强制终止安装。\n日志：" + log);
            using var process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = folder,
                Arguments = $"{(exact ? "/repair" : "/install")} /norestart /log \"{log}\"",
            }) ?? throw new IOException("微软安装器未启动。");
            // Cancellation only cancels download. Never abandon or kill an active MSI/Burn installer.
            await process.WaitForExitAsync();
            var code = process.ExitCode;
            var after = await DotNetEnvironmentService.CheckAsync();
            return ExitMessage(code) + "\n" + after.Summary + "\n日志：" + log;
        }
        catch (OperationCanceledException) { return "下载已取消或网络等待超时，未强制结束任何安装程序。"; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "已取消管理员授权，未启动安装。"; }
        catch (Exception ex) { Log.Error(".NET 环境修复失败", ex); return ".NET 修复未完成：" + ex.Message; }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException(".NET 元数据大小超限。");
        await using var stream = await content.ReadAsStreamAsync(token);
        using var data = new MemoryStream();
        var buffer = new byte[81920]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (data.Length + read > maximum) throw new InvalidDataException(".NET 元数据大小超限。");
            data.Write(buffer, 0, read);
        }
        return data.ToArray();
    }
}
