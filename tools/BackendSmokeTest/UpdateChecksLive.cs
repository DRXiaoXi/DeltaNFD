using DeltaNFD.Services;

/// <summary>
/// 按需联网的更新检查（--update-live）：只做只读检查，不下载、不安装。
/// 发布 update.json 之后用它确认真实网络下清单可读、版本比较与地址校验都正常。
/// 不放进 CI（避免网络抖动导致构建失败）。
/// </summary>
internal static class UpdateChecksLive
{
    internal static async Task RunAsync()
    {
        Console.WriteLine("=== 自动更新联网校验（只读：不下载、不安装） ===");

        var service = new UpdateService();
        // The harness is not an installed copy: exercise the real source without modifying settings or bypassing installation guards.
        var result = await service.CheckSourcesAsync(service.Config, new AppSettings(), service.CurrentVersion, true,
            (url, ct) => UpdateService.FetchUpdateSourceAsync(url, service.Config, ct));

        Console.WriteLine($"  状态：{result.Status}");
        Console.WriteLine($"  说明：{result.Message}");
        Console.WriteLine($"  当前版本：{result.CurrentVersionText}");
        Console.WriteLine($"  支持自动更新：{(service.IsInstalledCopy ? "是（安装版）" : "否（开发构建/手动解压）")}");
        Console.WriteLine($"  更新源来自：{service.ConfigSourceDescription}");
        Console.WriteLine($"  清单地址：{string.Join("；", service.Config.ManifestUrls ?? new List<string>())}");
        if (result.Manifest is { } published)
        {
            Console.WriteLine($"  远端已发布最高版本：{published.Version}");
            foreach (var current in new[] { new Version(0, 82, 0), new Version(0, 83, 0) })
            {
                var scenario = service.Evaluate(published, new AppSettings(), current);
                Console.WriteLine($"  只读模拟本机 {current}：{scenario.Status}");
            }
        }

        if (result.HasUpdate)
        {
            Console.WriteLine($"  可用版本：{result.AvailableVersionText}（强制={result.Mandatory}）");
            Console.WriteLine($"  发布页面：{result.ReleasePageUrl}");
            Console.WriteLine("  更新说明：");
            Console.WriteLine(result.Notes);
            foreach (var installer in result.Manifest?.Installers ?? new List<UpdateInstallerInfo>())
            {
                Console.WriteLine($"    · {installer.Url}");
                Console.WriteLine($"      {installer.SizeBytes} 字节 · sha256={installer.Sha256}");
            }

            Console.WriteLine();
            Console.WriteLine("（联网校验到此为止：下载与安装必须由用户在界面确认）");
        }

        if (result.Status is UpdateCheckStatus.NetworkError or UpdateCheckStatus.ManifestInvalid)
        {
            Environment.ExitCode = 1;
        }
    }
}
