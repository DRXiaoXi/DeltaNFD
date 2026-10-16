using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>
/// 虚拟化环境检测（纯只读）。硬件标准保留固件 CPU 虚拟化与当前内核 DMA 保护。
/// Windows 运行状态、配置提醒和读取失败分别处理；不是反作弊通过保证。
/// DMA 保护不依赖 VBS；DMA 保护关闭不能单独证明固件 IOMMU 已关闭。
/// </summary>
public static class AceVirtualizationCheck
{
    private const string DeviceGuardPolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard";

    /// <summary>与虚拟化有关的 Windows 可选功能；安装状态不证明正在运行。</summary>
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

        var result = Evaluate(processor, dg, features);
        Log.Info("虚拟化环境只读检测：" + string.Join("；", result.Hardware.Concat(result.System).Select(i => $"{i.Id}={i.Status}:{i.Detail}")));
        return result;
    }

    internal static AceVirtualizationCheckResult Evaluate(ProcessorInfo processor, DeviceGuardInfo dg, FeatureInfo[] features)
    {
        var hardware = BuildHardwareItems(processor, dg);
        var system = BuildSystemItems(processor, dg, features);

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
        if (processor.FirmwareVirtualization is null || processor.FirmwareVirtualization == false
            && (processor.HypervisorPresent == true || dg.VbsStatus == 2 || dg.HvciRunning == true))
        {
            items.Add(new AceVirtCheckItem
            {
                Id = "hardware.cpu-virt", Name = "CPU 虚拟化（VT-x / SVM）", Category = "hardware",
                Status = AceVirtStatus.Unknown,
                Detail = "无法确认固件虚拟化开关（系统未返回，或 Hypervisor 运行时字段被隐藏）",
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

        // 保留当前内核 DMA 保护开启这一硬件标准，不以能力或注册表策略替代。
        items.Add(dg.KernelDmaProtection switch
        {
            null => new AceVirtCheckItem
            {
                Id = "hardware.iommu", Name = "内核 DMA 保护（硬件安全标准）", Category = "hardware",
                Status = AceVirtStatus.Unknown,
                Detail = "无法查询内核 DMA 保护状态（可能未以管理员运行或系统接口缺失）",
                Advice = "以管理员身份运行本工具后重新检测；或打开「系统信息」(msinfo32) 查看「内核 DMA 保护」。",
            },
            true => new AceVirtCheckItem
            {
                Id = "hardware.iommu", Name = "内核 DMA 保护（硬件安全标准）", Category = "hardware",
                Status = AceVirtStatus.Pass,
                Detail = "Windows 当前已开启内核 DMA 保护",
                Advice = "",
            },
            false => new AceVirtCheckItem
            {
                Id = "hardware.iommu", Name = "内核 DMA 保护（硬件安全标准）", Category = "hardware",
                Status = AceVirtStatus.Fail,
                Detail = "Windows 当前未开启内核 DMA 保护",
                Advice = "通过 msinfo32 复核「内核 DMA 保护」，并核对 BIOS 的 VT-d / IOMMU 与厂商固件支持；关闭并不能单独证明 IOMMU 未开启。",
            },
        });

        return items;
    }

    // ---------------- 系统层 ----------------

    private static List<AceVirtCheckItem> BuildSystemItems(ProcessorInfo processor, DeviceGuardInfo dg, FeatureInfo[] features)
    {
        var items = new List<AceVirtCheckItem>();

        items.Add(new AceVirtCheckItem
        {
            Id = "system.hypervisor-runtime", Name = "Hypervisor 当前运行状态", Category = "system",
            Status = processor.HypervisorPresent switch
            {
                true => AceVirtStatus.Fail,
                false when dg.VbsStatus == 2 || dg.HvciRunning == true => AceVirtStatus.Unknown,
                false => AceVirtStatus.Pass,
                null => AceVirtStatus.Unknown,
            },
            Detail = processor.HypervisorPresent switch
            {
                true => "系统报告 Hypervisor 正在运行，未满足本工具的无 Hypervisor 标准",
                false when dg.VbsStatus == 2 || dg.HvciRunning == true => "系统与 Device Guard 的运行状态不一致，请重新检测",
                false => "本次开机未检测到 Hypervisor 运行",
                null => "系统未返回 Hypervisor 运行状态",
            },
            Advice = processor.HypervisorPresent == true ? "如需满足此兼容性标准，可关闭 Hyper-V / VBS 并重启；这会影响虚拟机和相关 Windows 安全功能。" : "",
        });

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
            1 => new AceVirtCheckItem
            {
                Id = "system.vbs", Name = "基于虚拟化的安全性（VBS）", Category = "system",
                Status = AceVirtStatus.Warn, Detail = "已配置启用，但当前未运行",
                Advice = "配置不代表当前占用虚拟化；后续启动状态可能变化。",
            },
            2 => new AceVirtCheckItem
            {
                Id = "system.vbs", Name = "基于虚拟化的安全性（VBS）", Category = "system",
                Status = processor.HypervisorPresent == false ? AceVirtStatus.Unknown : AceVirtStatus.Fail,
                Detail = processor.HypervisorPresent == false ? "Device Guard 报告 VBS 运行，但系统报告 Hypervisor 未运行，需复核" : "已启用并正在运行",
                Advice = processor.HypervisorPresent == false ? "运行状态不一致，请重新检测并通过 msinfo32 复核。" : "管理员 CMD 执行 bcdedit /set hypervisorlaunchtype off 并重启；" +
                         "若仍运行，检查组策略「计算机配置 → 管理模板 → 系统 → Device Guard → 基于虚拟化的安全」设为已禁用。",
            },
            _ => new AceVirtCheckItem
            {
                Id = "system.vbs", Name = "基于虚拟化的安全性（VBS）", Category = "system",
                Status = AceVirtStatus.Unknown, Detail = "系统返回了无法识别的 VBS 状态", Advice = "通过 msinfo32 复核。",
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
                Status = processor.HypervisorPresent == false ? AceVirtStatus.Unknown : AceVirtStatus.Fail,
                Detail = processor.HypervisorPresent == false ? "Device Guard 报告 HVCI 运行，但系统报告 Hypervisor 未运行，需复核" : "运行中",
                Advice = processor.HypervisorPresent == false ? "运行状态不一致，请重新检测并核对 Windows 安全中心。" : "打开「Windows 安全中心 → 设备安全性 → 内核隔离详细信息」，关闭「内存完整性」后重启。",
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

        // 引导配置不等于本次开机运行状态，也不能单独证明有强制策略。
        if (dg.HypervisorLaunchTypeKnown && dg.HypervisorLaunchOff && processor.HypervisorPresent != true && dg.VbsStatus != 2)
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
                Detail = "已设为 off，但本次开机仍检测到虚拟化运行",
                Advice = "配置与本次启动状态不同，请重启后复核；不能仅凭此判断有策略强制。",
            });
        }
        else if (dg.HypervisorLaunchTypeKnown)
        {
            items.Add(new AceVirtCheckItem
            {
                Id = "system.hypervisor-launch", Name = "hypervisor 开机启动（bcdedit）", Category = "system",
                Status = AceVirtStatus.Warn,
                Detail = "配置为 Auto 或默认启动；不代表本次开机已运行",
                Advice = "实际是否占用以 Hypervisor / VBS 运行状态为准。",
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

        // 可选功能与待完成变更仅作为配置提醒。
        foreach (var feature in features)
        {
            items.Add(feature.State switch
            {
                FeatureState.Enabled => new AceVirtCheckItem
                {
                    Id = "system.feature." + feature.Id, Name = feature.Label, Category = "system",
                    Status = AceVirtStatus.Warn,
                    Detail = "组件已启用；实际是否占用以运行状态为准",
                    Advice = "仅安装组件无需处理；先核对 Hypervisor / VBS 是否正在运行。",
                },
                FeatureState.Pending => new AceVirtCheckItem
                {
                    Id = "system.feature." + feature.Id, Name = feature.Label, Category = "system",
                    Status = AceVirtStatus.Warn, Detail = "功能变更等待完成，不能当作已关闭", Advice = "重启后复核。",
                },
                FeatureState.Disabled or FeatureState.NotPresent => new AceVirtCheckItem
                {
                    Id = "system.feature." + feature.Id, Name = feature.Label, Category = "system",
                    Status = AceVirtStatus.Pass,
                    Detail = feature.State == FeatureState.NotPresent ? "当前系统未提供此组件" : "未启用",
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

        // 服务仅提供辅助信息，不单独证明 Hypervisor 已加载。
        foreach (var service in dg.RunningHyperVServices)
        {
            items.Add(new AceVirtCheckItem
            {
                Id = "system.service." + service, Name = $"Hyper-V 服务 {service}", Category = "system",
                Status = AceVirtStatus.Warn,
                Detail = "服务正在运行；服务状态不能单独证明 Hypervisor 已运行",
                Advice = "请结合 Hypervisor / VBS 运行状态判断。",
            });
        }

        // 策略配置不等于当前运行状态。
        if (dg.PolicyForcesVbs)
        {
            items.Add(new AceVirtCheckItem
            {
                Id = "system.deviceguard-policy", Name = "Device Guard 组策略", Category = "system",
                Status = AceVirtStatus.Warn,
                Detail = "组策略已配置启用 VBS；配置不等于当前运行",
                Advice = "企业设备请联系管理员核对；不要仅凭此配置判断当前环境不合格。",
            });
        }

        return items;
    }

    // ---------------- 只读查询实现 ----------------

    internal sealed record ProcessorInfo(bool? FirmwareVirtualization, string BoardInfo, bool? HypervisorPresent = null);

    internal enum FeatureState { Unknown, Enabled, Disabled, Pending, NotPresent }

    internal sealed record FeatureInfo(string Id, string Label, FeatureState State);

    internal sealed record DeviceGuardInfo(
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
            "$ErrorActionPreference='Stop'; " +
            "$cpu = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1; " +
            "$cs  = Get-CimInstance -ClassName Win32_ComputerSystem; " +
            "$bb  = Get-CimInstance -ClassName Win32_BaseBoard; " +
            "$out = [ordered]@{ " +
            "  virt = $(if ($null -ne $cpu -and $null -ne $cpu.VirtualizationFirmwareEnabled) { [int][bool]$cpu.VirtualizationFirmwareEnabled } else { -1 }); " +
            "  hypervisor = $cs.HypervisorPresent; " +
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
                boardInfo,
                root.TryGetProperty("hypervisor", out var h) ? h.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => (bool?)null } : null);
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
        // DeviceGuard exposes capabilities, not current Kernel DMA Protection. Query DMA separately.
        const string script =
            "$ErrorActionPreference='Stop'; " +
            "$dg = Get-CimInstance -Namespace root\\Microsoft\\Windows\\DeviceGuard -ClassName Win32_DeviceGuard | Select-Object -First 1; " +
            "@{vbs=$dg.VirtualizationBasedSecurityStatus; running=$dg.SecurityServicesRunning} | ConvertTo-Json -Compress";

        var hypervisorTask = QueryHypervisorLaunchTypeAsync();
        var servicesTask = QueryHyperVServicesAsync();
        var (code, stdout, _) = await RunPowerShellAsync(script, TimeSpan.FromSeconds(30));
        var launch = await hypervisorTask;
        var runningServices = await servicesTask;
        var dma = QueryKernelDmaProtection();

        // 组策略：HKLM\SOFTWARE\Policies\Microsoft\Windows\DeviceGuard\EnableVirtualizationBasedSecurity = 1
        var policyForcesVbs = ReadDword("HKLM", DeviceGuardPolicyPath, "EnableVirtualizationBasedSecurity") == 1;

        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return new DeviceGuardInfo(null, null, null, dma, launch.Known, launch.Off, policyForcesVbs, runningServices);
        }

        var guard = ParseGuard(stdout);
        return new DeviceGuardInfo(
            guard.Vbs, guard.Hvci, guard.Credential, dma,
            launch.Known, launch.Off, policyForcesVbs, runningServices);
    }

    internal static (int? Vbs, bool? Hvci, bool? Credential) ParseGuard(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            var root = doc.RootElement;
            int? vbs = root.TryGetProperty("vbs", out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out var n) && n is >= 0 and <= 2 ? n : null;
            bool? hvci = null, credential = null;
            if (root.TryGetProperty("running", out var running) && running.ValueKind == JsonValueKind.Array)
            {
                var numbers = new List<int>();
                foreach (var value in running.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number is < 0 or > 7)
                        return (vbs, null, null);
                    numbers.Add(number);
                }
                if (numbers.Contains(0) && numbers.Any(x => x != 0)) return (null, null, null);
                hvci = numbers.Contains(2); credential = numbers.Contains(1);
                if (vbs is 0 or 1 && (hvci == true || credential == true)) return (null, null, null);
            }
            return (vbs, hvci, credential);
        }
        catch { return (null, null, null); }
    }

    // Optional native read-only ABI: winsiderss/phnt ntexapi.h, information class 202, one BOOLEAN.
    // Unsupported responses stay Unknown. Capability/registry policy cannot prove current protection.
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQuerySystemInformation(int informationClass, out byte flag, uint size, out uint length);

    private static bool? QueryKernelDmaProtection()
    {
        try
        {
            var status = NtQuerySystemInformation(202, out var flag, 1, out var length);
            Log.Info($"内核 DMA 保护只读查询：NTSTATUS=0x{status:X8}, length={length}, flag={flag}");
            return ParseDmaResult(status, flag, length);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return null; }
    }

    internal static bool? ParseDmaResult(int status, byte flag, uint length)
        => status == 0 && length == 1 && flag <= 1 ? flag == 1 : null;

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
            "$ErrorActionPreference='Stop'; " +
            $"$wanted = @(\"{names}\"); " +
            "$all = @(Get-WindowsOptionalFeature -Online); if ($all.Count -eq 0) { exit 1 }; " +
            "$items = @($all | Where-Object { $wanted -contains $_.FeatureName } | " +
            "  Select-Object FeatureName,@{Name='State';Expression={$_.State.ToString()}}); " +
            "@{complete=$true; items=$items} | ConvertTo-Json -Depth 4 -Compress";

        var (code, stdout, _) = await RunPowerShellAsync(script, TimeSpan.FromSeconds(45));
        return ParseFeatures(code == 0 ? stdout ?? "" : "");
    }

    internal static FeatureInfo[] ParseFeatures(string json)
    {
        var states = new Dictionary<string, FeatureState>(StringComparer.OrdinalIgnoreCase);
        var complete = false;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json.Trim());
                var root = doc.RootElement;
                if (!root.TryGetProperty("complete", out var done) || done.ValueKind != JsonValueKind.True
                    || !root.TryGetProperty("items", out var rows) || rows.ValueKind != JsonValueKind.Array)
                    throw new FormatException();
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) throw new FormatException();
                    var name = row.TryGetProperty("FeatureName", out var n) ? n.GetString() : null;
                    var state = row.TryGetProperty("State", out var st) ? st.GetString() : null;
                    if (name is null || states.ContainsKey(name)) throw new FormatException();
                    states[name] = state switch
                    {
                        "Enabled" => FeatureState.Enabled,
                        "Disabled" or "DisabledWithPayloadRemoved" => FeatureState.Disabled,
                        "EnablePending" or "DisablePending" => FeatureState.Pending,
                        _ => FeatureState.Unknown,
                    };
                }
                complete = true;
            }
            catch
            {
                // JSON 解析失败 → 全部 Unknown
                states.Clear();
            }
        }

        return ConflictFeatures
            .Select(f => new FeatureInfo(
                f.Feature, f.Label,
                !complete ? FeatureState.Unknown : states.TryGetValue(f.Feature, out var state) ? state : FeatureState.NotPresent))
            .ToArray();
    }

    /// <summary>查询 hypervisor 开机启动类型；bcdedit 需要管理员。</summary>
    private static async Task<(bool Known, bool Off)> QueryHypervisorLaunchTypeAsync()
    {
        var (code, stdout, _) = await RunProcessCaptureAsync(
            "bcdedit.exe", "/enum {current}", TimeSpan.FromSeconds(20));
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return (false, false);
        }

        foreach (var line in SplitLines(stdout))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("hypervisorlaunchtype", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["hypervisorlaunchtype".Length..].Trim();
                return value.Equals("Off", StringComparison.OrdinalIgnoreCase) ? (true, true)
                    : value.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? (true, false) : (false, false);
            }
        }

        // 未设置过 = 默认 auto
        return stdout.Contains("winload", StringComparison.OrdinalIgnoreCase) ? (true, false) : (false, false);
    }

    // ---------------- 工具 ----------------

    private static int? ReadDword(string hive, string keyPath, string valueName)
    {
        try
        {
            using var key = hive == "HKCU"
                ? Registry.CurrentUser.OpenSubKey(keyPath)
                : Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is int value ? value : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }

    private static async Task<(int ExitCode, string? StdOut, string? StdErr)> RunPowerShellAsync(
        string command, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments =
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " + command)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        using var process = TryStartDiagnostic(psi);
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

        using var process = TryStartDiagnostic(psi);
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

    private static Process? TryStartDiagnostic(ProcessStartInfo startInfo)
    {
        try { return Process.Start(startInfo); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn("无法启动只读虚拟化诊断进程：" + Path.GetFileName(startInfo.FileName) + "；" + ex.Message);
            return null;
        }
    }

    private static string[] SplitLines(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
