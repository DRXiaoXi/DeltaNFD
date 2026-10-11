# 紧急算法策略发布

客户端从本仓库 `main/emergency/public.json` 和 `main/emergency/internal.json` 读取**签名包**，不把这里的 JSON 当作脚本、DLL 或安装器运行。

- `public.json`：Beta/Release 的公共策略，内测与公测均接收。
- `internal.json`：Alpha 内测策略，仅内测接收；内测同时应用公共策略。
- 默认启用独立的「接收紧急算法策略」开关。已安装版本启动及运行期间每 15 分钟检查；手动「检查更新」也检查。关闭后立即忽略缓存，打开后重新检查。
- 策略检查与整包版本检查并行，策略源超时不会挡住整包修复更新；设置页分别显示两者的结果。
- 需要先安装包含本机制的新客户端。已经发布的 Beta0.91 不具备该能力，不能靠服务器文件向旧客户端补装解释器。
- 尚未发布策略时 URL 可以返回 404，客户端使用内置算法。不要上传伪造、空文件或未签名的模板。

## v1 已接入的能力

| 字段 | 作用 / 限制 |
|---|---|
| `blockedCommands` | 暂停下表列出的后续操作；不取消已经开始的任务，不回放任务 |
| `memoryCleanMode` | `default` 沿用用户自动清理设置；`manual-only` 暂停自动清理，保留手动入口 |
| `memoryCleanCooldownSeconds` | 自动内存清理冷却，600–3600 秒；缺省保持原 600 秒 |
| `memoryCleanThresholdPercent` | 自动清理阈值，30–95%；仅能提高用户原阈值 |
| `dwmRestartDelaySeconds` | 游戏启动后的自动 DWM 重启延迟，5–120 秒；缺省保持原 5 秒 |

允许停用的命令：`ace.clean`、`runtime.repair`、`runtime.compatibility.repair`、`runtime.redist.uninstall`、`runtime.guard.enable`、`cpuframe.dwm.restart`、`cpuframe.memory.clean`、`system.cleanup.run`、`system.cleanup.shader-cache`。

其他命令不在当前能力范围。特别是停止、还原、核对、恢复、解除防护及退出不允许停用。前端发请求前和后端进入执行前都检查；DWM 自动重启与内存自动清理也检查。远端不能开启用户原本关闭的功能、改变路径、执行任意命令，或更改包校验结果。通用进程管理及响应性保护尚不属于 v1 策略能力，后续需要先增加经过验证的客户端接入点。

多通道策略同时生效时，停用列表取并集，内存冷却/阈值和 DWM 延迟取更保守值。任一有效包指定 `manual-only` 即暂停自动清理。

## 签名密钥

公钥固定在 `src/DeltaNFD.Shared/Services/EmergencyPolicyTrust.cs`。当前 KeyId：`emergency-2026-01`。对应私钥位于本机当前 Windows 用户的 CNG 密钥存储，名称 `DeltaNFD.Emergency.20261010`，不可导出；源码和策略包均不含私钥。仅 HTTPS、哈希或 GitHub 账号权限不能代替此签名。

本机用户配置文件或签名密钥丢失后，无法在另一台机器上直接签名；必须保留此签名环境，或先通过整包更新增补新的受信任公钥。不要在网络配置中加入动态公钥，也不要重新生成同名密钥来替代已有密钥。

```powershell
dotnet build tools/EmergencyPolicyPublisher -c Release -p:Platform=x64
dotnet tools/EmergencyPolicyPublisher/bin/x64/Release/net8.0-windows/EmergencyPolicyPublisher.dll sign DeltaNFD.Emergency.20261010 policy.json emergency/public.json
```

该命令只签名写文件，不提交、不上传、不发布。首次部署到其他签名环境时，可用 `init <新密钥名> <公钥输出路径>` 创建密钥，但必须先将新公钥固定进客户端并发布新客户端；不能直接给已发行客户端换公钥。

## 策略内容模板

此模板**不能直接发布**：先按实际日期、版本范围和线上最高序号填写，确认内容后签名。

```json
{
  "schemaVersion": 1,
  "engineVersion": 1,
  "sequence": 1,
  "id": "memory-safety-001",
  "channel": "public",
  "minimumAppVersion": "0.91",
  "maximumAppVersion": "0.91.0.0",
  "issuedUtc": "2026-10-10T00:00:00Z",
  "expiresUtc": "2026-10-17T00:00:00Z",
  "message": "临时改用手动内存清理，等待整包修复。",
  "blockedCommands": [],
  "memoryCleanMode": "manual-only",
  "memoryCleanCooldownSeconds": 1200,
  "memoryCleanThresholdPercent": 85,
  "dwmRestartDelaySeconds": 10
}
```

适用版本为内部数值版本、包含上下界；`0.91` 与 `0.91.0.0` 等价。引擎/格式版本当前只能是 1。有效期最长 30 天，签发时间不得超过客户端当前时间 5 分钟；尚未到签发时间的策略暂不生效。未知字段、重复字段、未知能力及越界参数整包拒绝。

## 撤回、断网与诊断

每个通道的 `sequence` 必须单调递增。撤回时发布**更高序号**的空策略：`blockedCommands: []`、`memoryCleanMode: "default"`、其他可选参数留空。仅删除服务器文件不能撤回已缓存策略，旧序号也不能回滚；可用更高序号重新发布此前安全参数。要撤回两个通道需分别发包。

签名包最大 64 KiB、内容最大 32 KiB；HTTPS 重定向逐跳检查固定主机白名单，最多 3 次。GitHub 加速开启时允许失败后使用固定代理，内容仍必须通过签名。

有效缓存存于 `%APPDATA%/Delta NFD/emergency-policies/`。断网/拒绝新包时继续使用有效缓存；过期、通道不符、版本不符则回到内置算法。过期包也保留为最高序号依据。损坏包保留证据，不静默删除或覆盖。设置页显示最近检查及当前有效策略，日志记录接收或拒绝原因。

策略只影响后续决策，不中断正在执行的安装/修复，不强杀进程，不删除恢复记录。新策略或关闭开关不会撤销已经完成的系统修改；沿用原来的恢复流程。

```powershell
dotnet run --project tools/EmergencyPolicyChecks -c Release -p:Platform=x64 --
```

测试使用独立临时目录及测试密钥，不读取真实设置、不联网，不执行系统维护。
