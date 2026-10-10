using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>
/// ACE 虚拟化环境检测（纯只读）。对应腾讯游戏安全官方指引
/// 《解决"未开启或有其他软件占用CPU虚拟化功能"弹框》（gamesafe.qq.com/article/1181.shtml）：
///
/// 硬件层（需在 BIOS 开启）：
///   Intel → VT-x（Virtualization Technology Extensions）+ VT-d（直接 I/O 虚拟化）
///   AMD   → SVM（Secure Virtual Machine）+ IOMMU
///   检测口径：Win32_Processor.VirtualizationFirmwareEnabled（= msinfo32「Hyper-V 固件中启用的虚拟化」）
///   + 内核 DMA 保护（启用 = VT-d/IOMMU 已在固件开启并被系统使用）。
///
/// 系统层（需在 Windows 内关闭/排除）：
///   VBS（Win32_DeviceGuard 运行时状态 + bcdedit hypervisorlaunchtype + 组策略复核）、
///   内核隔离 HVCI、Hyper-V / 虚拟机平台 / Windows 沙盒等可选功能、
///   vmms / HvHost 服务、Device Guard 组策略。
///
/// 关键认知：内核 DMA 保护启用是 ACE **要求**的状态（VT-d/IOMMU 已开），
/// 与「VBS 关闭」不冲突——DMA 保护工作并不需要 hypervisor 运行。
/// </summary>
public static class AceVirtualizationCheck
{
    private const string DeviceGuardPolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard";

    /// <summary>占用 CPU 虚拟化、会触发 ACE 弹框的 Windows 可选功能。</summary>
    private static readonly (string Feature, string Label)[] ConflictFeatures =
    [
        ("Microsoft-Hyper-V-All", "Hyper-V"),
        ("VirtualMachinePlatform", "虚拟机平台（WSL2 / WSA 依赖）"),
        ("HypervisorPlatform", "Windows 虚拟机监控程序平台"),
        ("Containers-DisposableClientVM", "Windows 沙盒"),
    ];

    public static async Task<AceVirtualizationCheckResult> RunAsync()
    {
        // 三路外部查询并行：处理器/主板（CIM）、DeviceGuard（VBS/HVCI/DMA）、可选功能状态（DISM）。
        var processorTask = QueryProcessorAsync();
        var deviceGuardTask = QueryDeviceGuardAsync();
        var featuresTask = QueryConflictFeaturesAsync();
        await Task.WhenAll(processorTask, deviceGuardTask, featuresTask);

        var processor = processorTask.Result;
        var dg = deviceGuardTask.Result;
        var features = featuresTask.Result;

        var hardware = BuildHardwareItems(processor, dg);
        var system = BuildSystemItems(dg, features);

        return new AceVirtualizationCheckResult
        {
            Hardware = hardware,
            System = system,
            BoardInfo = processor.BoardInfo,
        };
    }

    // ---------------- 硬件层 ----------------

    private static List<AceVirtCheckItem> BuildHardwareItems(ProcessorInfo processor, DeviceGuardInfo dg)
    {
        var items = new List<AceVirtCheckItem>();

        // CPU 虚拟化（Intel VT-x / AMD SVM）：固件开关状态。
        if (processor.FirmwareVirtualization is null)
        {
            items.Add(new AceVirtCheckItem
            {
                Id = "hardware.cpu-virt", Name = "CPU 虚拟化（VT-x / SVM）", Category = "hardware",
                Status = AceVirtStatus.Unknown,
                Detail = "无法读取固件虚拟化状态（系统接口未返回）",
                Advice = "打开「系统信息」(msinfo32) 查看「Hyper-V 固件中启用的虚拟化」；若为「否」，需进 BIOS 开启。",
            });
        }
        else if (processor.FirmwareVirtualization == true)
        {
            items.Add(new AceVirtCheckItem
            {
                Id = "hardware.cpu-virt", Name = "CPU 虚拟化（VT-x / SVM）", Category = "hardware",
                Status = AceVirtStatus.Pass,
                Detail = "已在 BIOS 中启用",
                Advice = "",
            });
        }
        else
        {
            items.Add(new AceVirtCheckItem
            {
                Id = "hardware.cpu-virt", Name = "CPU 虚拟化（VT-x / SVM）", Category = "hardware",
                Status = AceVirtStatus.Fail,
                Detail = "BIOS 中未开启（固件虚拟化已关闭）",
                Advice = "重启进 BIOS 开启：Intel 平台开「Virtualization Technology (VT-x)」，" +
                         "AMD 平台开「SVM / AMD Secure Virtual Machine」。保存退出（一般 F10）后重新检测。",
            });
        }

        // IOMMU（Intel VT-d / AMD IOMMU）：内核 DMA 保护启用 = 固件已开且系统在用。
        // 注意：这是 ACE 要求开启的项，不是冲突项；DMA 保护不依赖 hypervisor。
        items.Add(dg.KernelDmaProtection switch
        {
            null => new AceVirtCheckItem
            {
                Id = "hardware.iommu", Name = "IOMMU（VT-d / AMD-IOMMU，内核 DMA 保护）", Category = "hardware",
                Status = AceVirtStatus.Unknown,
                Detail = "无法查询内核 DMA 保护状态（可能未以管理员运行或系统接口缺失）",
                Advice = "以管理员身份运行本工具后重新检测；或打开「系统信息」(msinfo32) 查看「内核 DMA 保护」。",
            },
            true => new AceVirtCheckItem
            {
                Id = "hardware.iommu", Name = "IOMMU（VT-d / AMD-IOMMU，内核 DMA 保护）", Category = "hardware",
                Status = AceVirtStatus.Pass,
                Detail = "内核 DMA 保护已启用（VT-d / IOMMU 已开启）",
                Advice = "",
            },
            false => new AceVirtCheckItem
            {
                Id = "hardware.iommu", Name = "IOMMU（VT-d / AMD-IOMMU，内核 DMA 保护）", Category = "hardware",
                Status = AceVirtStatus.Fail,
                Detail = "内核 DMA 保护未启用（VT-d / IOMMU 未开启或固件未暴露）",
                Advice = "重启进 BIOS 开启：Intel 平台开「VT-d（Directed I/O 虚拟化）」，" +
                         "AMD 平台开「IOMMU」。开启后 Windows 会自动启用内核 DMA 保护。",
            },
        });

        return items;
    }

    // ---------------- 系统层 ----------------

    private static List<AceVirtCheckItem> BuildSystemItems(DeviceGuardInfo dg, FeatureInfo[] features)
    {
        var items = new List<AceVirtCheckItem>();

        // VBS 运行时状态（反映本次开机实际状态，是最权威口径）。
        items.Add(dg.VbsStatus switch
        {
            null => new AceVirtCheckItem
            {
                Id = "system.vbs", Name = "基于虚拟化的安全性（VBS）", Category = "system",
                Status = AceVirtStatus.Unknown,
                Detail = "无法查询 VBS 运行时状态（可能未以管理员运行或系统接口缺失）",
                Advice = "以管理员身份运行本工具后重新检测；或打开「系统信息」(msinfo32) 查看「基于虚拟化的安全性」。",
            },
            0 => new AceVirtCheckItem
            {
                Id = "system.vbs", Name = "基于虚拟化的安全性（VBS）", Category = "system",
                Status = AceVirtStatus.Pass,
                Detail = "未启用（本次开机未运行）",
                Advice = "",
            },
            _ => new AceVirtCheckItem
            {
                Id = "system.vbs", Name = "基于虚拟化的安全性（VBS）", Category = "system",
                Status = AceVirtStatus.Fail,
                Detail = dg.VbsStatus == 2 ? "已启用并正在运行" : "已配置启用（重启后生效）",
                Advice = "管理员 CMD 执行 bcdedit /set hypervisorlaunchtype off 并重启；" +
                         "若仍运行，检查组策略「计算机配置 → 管理模板 → 系统 → Device Guard → 基于虚拟化的安全」设为已禁用。",
            },
        });

        // 内核隔离（内存完整性 HVCI）。
        items.Add(dg.HvciRunning switch
        {
            null => new AceVirtCheckItem
            {
                Id = "system.hvci", Name = "内核隔离（内存完整性 HVCI）", Category = "system",
                Status = AceVirtStatus.Unknown,
                Detail = "无法查询 HVCI 运行时状态",
                Advice = "以管理员身份运行本工具后重新检测；或在「Windows 安全中心 → 设备安全性 → 内核隔离详细信息」查看。",
            },
            false => new AceVirtCheckItem
            {
                Id = "system.hvci", Name = "内核隔离（内存完整性 HVCI）", Category = "system",
                Status = AceVirtStatus.Pass,
                Detail = "未运行",
                Advice = "",
            },
            true => new AceVirtCheckItem
            {
                Id = "system.hvci", Name = "内核隔离（内存完整性 HVCI）", Category = "system",
                Status = AceVirtStatus.Fail,
                Detail = "运行中",
                Advice = "打开「Windows 安全中心 → 设备安全性 → 内核隔离详细信息」，关闭「内存完整性」后重启。",
            },
        });

        // Credential Guard（随 VBS 一起占用虚拟化）。
        if (dg.CredentialGuardRunning == true)
        {
            items.Add(new AceVirtCheckItem
            {
                Id = "system.credential-guard", Name = "Credential Guard", Category = "system",
                Status = AceVirtStatus.Warn,
                Detail = "运行中（随 VBS 占用 CPU 虚拟化）",
                Advice = "通常随 VBS 关闭而消失；企业设备可能由域策略强制开启，需联系管理员。",
            });
        }

        // hypervisor 引导配置（bcdedit）：off 但 VBS 仍运行说明有策略强制，不算过。
        if (dg.HypervisorLaunchTypeKnown && dg.HypervisorLaunchOff && dg.VbsStatus is null or 0)
        {
            items.Add(new AceVirtCheckItem
            {
                Id = "system.hypervisor-launch", Name = "hypervisor 开机启动（bcdedit）", Category = "system",
                Status = AceVirtStatus.Pass,
                Detail = "已关闭（hypervisorlaunchtype off）",
                Advice = "",
            });
        }
        else if (dg.HypervisorLaunchTypeKnown && dg.HypervisorLaunchOff)
        {
            items.Add(new AceVirtCheckItem
            {
                Id = "system.hypervisor-launch", Name = "hypervisor 开机启动（bcdedit）", Category = "system",
                Status = AceVirtStatus.Warn,
                Detail = "已设为 off，但 VBS 仍在运行（存在组策略/MDM 强制启用）",
                Advice = "检查「编辑组策略 → 计算机配置 → 管理模板 → 系统 → Device Guard」，" +
                         "将「基于虚拟化的安全」设为「已禁用」，重启后重新检测。",
            });
        }
        else if (dg.HypervisorLaunchTypeKnown)
        {
            items.Add(new AceVirtCheckItem
            {
                Id = "system.hypervisor-launch", Name = "hypervisor 开机启动（bcdedit）", Category = "system",
                Status = AceVirtStatus.Fail,
                Detail = "自动启动（hypervisorlaunchtype auto），hypervisor 会随开机加载",
                Advice = "管理员 CMD 执行 bcdedit /set hypervisorlaunchtype off 并重启。",
            });
        }
        else
        {
            items.Add(new AceVirtCheckItem
            {
                Id = "system.hypervisor-launch", Name = "hypervisor 开机启动（bcdedit）", Category = "system",
                Status = AceVirtStatus.Unknown,
                Detail = "无法读取引导配置（需以管理员身份运行）",
                Advice = "以管理员身份运行本工具后重新检测。",
            });
        }

        // Hyper-V / 虚拟机平台 / 沙盒等可选功能（启用 = 占用虚拟化，即使 hypervisor 暂未运行）。
        foreach (var feature in features)
        {
            items.Add(feature.State switch
            {
                FeatureState.Enabled => new AceVirtCheckItem
                {
                    Id = "system.feature." + feature.Id, Name = feature.Label, Category = "system",
                    Status = AceVirtStatus.Fail,
                    Detail = "已启用（会拉起 hypervisor 占用 CPU 虚拟化）",
                    Advice = feature.Id switch
                    {
                        "Containers-DisposableClientVM" =>
                            "打开「控制面板 → 程序和功能 → 启用或关闭 Windows 功能」，取消勾选「Windows 沙盒」后重启。",
                        "VirtualMachinePlatform" =>
                            "打开「启用或关闭 Windows 功能」，取消勾选「虚拟机平台」后重启。注意：WSL2 将无法使用。",
                        "HypervisorPlatform" =>
                            "打开「启用或关闭 Windows 功能」，取消勾选「Windows 虚拟机监控程序平台」后重启。",
                        _ =>
                            "打开「启用或关闭 Windows 功能」，取消勾选「Hyper-V」后重启；" +
                            "若取消不成功，先在「Windows 设置 → 帐户 → 登录选项」关闭面部/指纹并删除 PIN。",
                    },
                },
                FeatureState.Disabled => new AceVirtCheckItem
                {
                    Id = "system.feature." + feature.Id, Name = feature.Label, Category = "system",
                    Status = AceVirtStatus.Pass,
                    Detail = "未启用",
                    Advice = "",
                },
                _ => new AceVirtCheckItem
                {
                    Id = "system.feature." + feature.Id, Name = feature.Label, Category = "system",
                    Status = AceVirtStatus.Unknown,
                    Detail = "无法查询功能状态",
                    Advice = "以管理员身份运行本工具后重新检测。",
                },
            });
        }

        // Hyper-V 关键服务（vmms / HvHost）：运行中 = hypervisor 生态在占用。
        foreach (var service in dg.RunningHyperVServices)
        {
            items.Add(new AceVirtCheckItem
            {
                Id = "system.service." + service, Name = $"Hyper-V 服务 {service}", Category = "system",
                Status = AceVirtStatus.Warn,
                Detail = "运行中",
                Advice = "管理员 CMD 执行 sc stop " + service + " 并 sc config " + service +
                         " start= demand；或直接用「系统优化」页的「关闭 Hyper-V / VBS」一键处理。",
            });
        }

        // Device Guard 组策略（策略强制启用时，改注册表/bcdedit 都会被覆盖）。
        if (dg.PolicyForcesVbs)
        {
            items.Add(new AceVirtCheckItem
            {
                Id = "system.deviceguard-policy", Name = "Device Guard 组策略", Category = "system",
                Status = AceVirtStatus.Fail,
                Detail = "组策略已配置「基于虚拟化的安全」（会覆盖手动关闭）",
                Advice = "运行 gpedit.msc →「计算机配置 → 管理模板 → 系统 → Device Guard」→" +
                         "将「基于虚拟化的安全」设为「已禁用」，重启后重新检测。",
            });
        }

        return items;
    }

    // ---------------- 只读查询实现 ----------------

    private sealed record ProcessorInfo(bool? FirmwareVirtualization, string BoardInfo);

    private enum FeatureState { Unknown, Enabled, Disabled }

    private sealed record FeatureInfo(string Id, string Label, FeatureState State);

    private sealed record DeviceGuardInfo(
        int? VbsStatus,
        bool? HvciRunning,
        bool? CredentialGuardRunning,
        bool? KernelDmaProtection,
        bool HypervisorLaunchTypeKnown,
        bool HypervisorLaunchOff,
        bool PolicyForcesVbs,
        string[] RunningHyperVServices);

    /// <summary>固件虚拟化开关（VT-x/SVM）+ 主板信息（引导用户进 BIOS）。</summary>
    private static async Task<ProcessorInfo> QueryProcessorAsync()
    {
        const string script =
            "$ErrorActionPreference='SilentlyContinue'; " +
            "$cpu = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1; " +
            "$cs  = Get-CimInstance -ClassName Win32_ComputerSystem; " +
            "$bb  = Get-CimInstance -ClassName Win32_BaseBoard; " +
            "$out = [ordered]@{ " +
            "  virt = $(if ($null -ne $cpu -and $null -ne $cpu.VirtualizationFirmwareEnabled) { [int][bool]$cpu.VirtualizationFirmwareEnabled } else { -1 }); " +
            "  manufacturer = [string]($cs.Manufacturer); " +
            "  model = [string]($cs.Model); " +
            "  board = ([string]($bb.Manufacturer) + ' ' + [string]($bb.Product)).Trim() " +
            "}; " +
            "ConvertTo-Json -InputObject $out -Compress";

        var (code, stdout, _) = await RunPowerShellAsync(script, TimeSpan.FromSeconds(30));
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return new ProcessorInfo(null, "");
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout.Trim());
            var root = doc.RootElement;
            var virt = root.TryGetProperty("virt", out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32() : -1;
            var manufacturer = root.TryGetProperty("manufacturer", out var m) ? m.GetString() ?? "" : "";
            var model = root.TryGetProperty("model", out var mo) ? mo.GetString() ?? "" : "";
            var board = root.TryGetProperty("board", out var b) ? b.GetString() ?? "" : "";
            var boardInfo = string.IsNullOrWhiteSpace(board) ? (manufacturer + " " + model).Trim() : board;
            return new ProcessorInfo(
                virt switch { 1 => true, 0 => false, _ => null },
                boardInfo);
        }
        catch
        {
            return new ProcessorInfo(null, "");
        }
    }

    private static async Task<DeviceGuardInfo> QueryDeviceGuardAsync()
    {
        // VirtualizationBasedSecurityStatus: 0=未启用 1=已启用未运行 2=已启用并运行
        // SecurityServicesRunning: 1=Credential Guard 2=HVCI（内存完整性）
        // KernelDmaProtection: 布尔实例属性；为兼容多实例/语言环境，先取第一个非空值。
        const string script =
            "$ErrorActionPreference='SilentlyContinue'; " +
            "$dg = Get-CimInstance -Namespace root\\Microsoft\\Windows\\DeviceGuard -ClassName Win32_DeviceGuard | Select-Object -First 1; " +
            "if ($null -eq $dg) { 'ERR' } else { " +
            "  [int]$dg.VirtualizationBasedSecurityStatus; " +
            "  (@($dg.SecurityServicesRunning) -join ','); " +
            "  $(if ($null -eq $dg.KernelDmaProtection) { -1 } else { [int][bool]$dg.KernelDmaProtection }) " +
            "}";

        var hypervisorTask = QueryHypervisorLaunchTypeAsync();
        var servicesTask = QueryHyperVServicesAsync();
        var (code, stdout, _) = await RunPowerShellAsync(script, TimeSpan.FromSeconds(30));
        var launch = await hypervisorTask;
        var runningServices = await servicesTask;

        // 组策略：HKLM\SOFTWARE\Policies\Microsoft\Windows\DeviceGuard\EnableVirtualizationBasedSecurity = 1
        var policyForcesVbs = ReadDword("HKLM", DeviceGuardPolicyPath, "EnableVirtualizationBasedSecurity") == 1
                              || ReadDword("HKLM", DeviceGuardPolicyPath, "HVCIMATRequired") == 1;

        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return new DeviceGuardInfo(null, null, null, null, launch.Known, launch.Off, policyForcesVbs, runningServices);
        }

        var lines = SplitLines(stdout);
        if (lines.FirstOrDefault() == "ERR")
        {
            return new DeviceGuardInfo(null, null, null, null, launch.Known, launch.Off, policyForcesVbs, runningServices);
        }

        var vbsStatus = int.TryParse(lines.ElementAtOrDefault(0), out var s) ? s : (int?)null;
        var running = (lines.ElementAtOrDefault(1) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hvciRunning = running.Contains("2");
        var credentialGuardRunning = running.Contains("1");
        var dma = int.TryParse(lines.ElementAtOrDefault(2), out var d) ? d : -1;

        return new DeviceGuardInfo(
            vbsStatus, hvciRunning, credentialGuardRunning,
            dma switch { 1 => true, 0 => false, _ => null },
            launch.Known, launch.Off, policyForcesVbs, runningServices);
    }

    /// <summary>Hyper-V 关键服务（vmms / HvHost）中实际在运行的。</summary>
    private static async Task<string[]> QueryHyperVServicesAsync()
    {
        var (code, stdout, _) = await RunPowerShellAsync(
            "$ErrorActionPreference='SilentlyContinue'; " +
            "Get-Service -Name vmms,HvHost | Where-Object { $_.Status -eq 'Running' } | Select-Object -ExpandProperty Name",
            TimeSpan.FromSeconds(20));
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        return SplitLines(stdout);
    }

    private static async Task<FeatureInfo[]> QueryConflictFeaturesAsync()
    {
        // 一次性取全部功能状态（JSON），避免每个功能一次 DISM 往返。
        var names = string.Join("\",\"", ConflictFeatures.Select(f => f.Feature));
        var script =
            "$ErrorActionPreference='SilentlyContinue'; " +
            $"$wanted = @(\"{names}\"); " +
            "$items = @(Get-WindowsOptionalFeature -Online | Where-Object { $wanted -contains $_.FeatureName } | " +
            "  Select-Object FeatureName,@{Name='State';Expression={$_.State.ToString()}}); " +
            "if ($items.Count -eq 0) { '[]' } else { ConvertTo-Json -InputObject $items -Compress }";

        var (code, stdout, _) = await RunPowerShellAsync(script, TimeSpan.FromSeconds(45));
        var states = new Dictionary<string, FeatureState>(StringComparer.OrdinalIgnoreCase);
        if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            try
            {
                using var doc = JsonDocument.Parse(stdout.Trim());
                // 单个结果时 ConvertTo-Json 输出对象而非数组。
                var rows = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray().ToArray()
                    : [doc.RootElement];
                foreach (var row in rows)
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    var name = row.TryGetProperty("FeatureName", out var n) ? n.GetString() : null;
                    var state = row.TryGetProperty("State", out var st) ? st.GetString() : null;
                    if (name is null) continue;
                    states[name] = state switch
                    {
                        "Enabled" or "EnablePending" => FeatureState.Enabled,
                        "Disabled" or "DisabledWithPayloadRemoved" or "DisablePending" => FeatureState.Disabled,
                        _ => FeatureState.Unknown,
                    };
                }
            }
            catch
            {
                // JSON 解析失败 → 全部 Unknown
            }
        }

        return ConflictFeatures
            .Select(f => new FeatureInfo(
                f.Feature, f.Label,
                states.TryGetValue(f.Feature, out var state) ? state : FeatureState.Unknown))
            .ToArray();
    }

    /// <summary>查询 hypervisor 开机启动类型；bcdedit 需要管理员。</summary>
    private static async Task<(bool Known, bool Off)> QueryHypervisorLaunchTypeAsync()
    {
        var (code, stdout, _) = await RunProcessCaptureAsync(
            "bcdedit.exe", "/enum {current}", TimeSpan.FromSeconds(20));
        if (code != 0 || stdout is null)
        {
            return (false, false);
        }

        foreach (var line in SplitLines(stdout))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("hypervisorlaunchtype", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["hypervisorlaunchtype".Length..].Trim();
                return (true, value.Equals("Off", StringComparison.OrdinalIgnoreCase));
            }
        }

        // 未设置过 = 默认 auto
        return (true, false);
    }

    // ---------------- 工具 ----------------

    private static int? ReadDword(string hive, string keyPath, string valueName)
    {
        using var key = hive == "HKCU"
            ? Registry.CurrentUser.OpenSubKey(keyPath)
            : Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue(valueName) is int value ? value : null;
    }

    private static async Task<(int ExitCode, string? StdOut, string? StdErr)> RunPowerShellAsync(
        string command, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments =
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                $"\"[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; {command}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (-1, null, "无法启动 powershell.exe");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 进程可能已自行退出 */ }
            return (-1, null, "PowerShell 执行超时");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessCaptureAsync(
        string fileName, string arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (-1, "", $"无法启动 {fileName}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 进程可能已自行退出 */ }
            return (-1, "", "执行超时");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string[] SplitLines(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
