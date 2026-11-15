# 第三方驱动与公开源码分离

源码与载荷分离门禁保留。Win10 主程序新增玩家自备原版 DPC EXE 的接入：只记录已核验路径，运行/正常关闭自己的进程，并只读核验服务与系统分辨率。新安装包不含 DPC 原件，也不附带旧自研测试驱动；历史 Alpha0.935 r3/r4 安装包保持。DPC 没有书面再分发授权，安全审查也未通过，仍不能生成携带该驱动的交付包。使用与限制见 `docs/Win10-DPC.md`。

## 玩家自备文件的研究插件（2026-11-14，本地未分发）

另交付一个本地研究插件 `src/Plugin.DpcTimerHold`（`.dnfdplugin` 包 `io.github.drxiaoxi.dpc-timer-hold` 1.0.0）。它**不包含任何 DPC 字节**，要求玩家自己提供原版 1.4.0 EXE 并按固定字节数/SHA256 核对，只启动/正常关闭该进程并只读核验 `dpclat_driver` 服务与分辨率；不直接驱动设备、不安装驱动、不改注册表/BCD/证书。包审计 190 项逐项 SHA256 与已知 DPC EXE/x86 SYS/x64 SYS 零命中。它不改变本文件的分离门禁，不产生原厂再分发授权，也不消除 CVE-2025-51619；不随产品分发。说明见 `notes/DPC计时器常驻Win10插件-20261114.md`，证据 `_buildcheck/dpc-timer-plugin-20261114/final-result.json`。

## 公开内容

保留本项目原创接入代码、`installer/driver-payloads.json` 中的版本/长度/SHA 和构建检查。第三方 SYS、原始 EXE、压缩副本和授权原件不入 Git、GitHub Source ZIP 或公开输入清单。

`.gitignore` 防止常用私有目录/文件误加；`.gitattributes` 给源码导出增加排除。它们不会移除已经提交的历史内容，不能替代内容审查。新增 `audit-index` 直接检查 Git 索引对象，未暂存的覆盖/删除不能隐藏已暂存的载荷；CI 在下载构建依赖之前运行。检查包含已知 DPC 原件及 x86/x64 驱动 SHA、DPC PE 标识和有限 ZIP/gzip 嵌套；不是任意格式/任意混淆的通用二进制扫描器。

```powershell
python tools/driver_payloads.py audit-index
python tools/check_repository.py --include-untracked
```

若检查发现载荷，停止提交/发布并人工移出索引。工具不自动修改 Git 或删除原件。源码 ZIP 可从审查通过的索引生成，使用 Git 对象字节并重新生成 `SOURCE_FILES_SHA256.json`，输出必须位于源码库之外、不得覆盖现有文件：

```powershell
python tools/driver_payloads.py source-archive --output D:\BuildOutput\DeltaNFD_Source.zip
```

该 ZIP 对应索引，不包含未暂存改动。对外发布前仍需把预期源码提交并核对提交身份；本轮没有暂存/提交或生成冒充当前开发版的源码包。

## 私有构建输入

未来已获授权的驱动应放在源码库之外，例如 `D:\PrivateBuildInputs\DpcTimer\dpclat_x64.sys`；授权原件同样留在私有位置。工具仅按固定长度和 SHA 只读核验，不执行或安装输入文件、不修改证书/启动策略、不自动下载：

```powershell
python tools/driver_payloads.py verify-private --input D:\PrivateBuildInputs\DpcTimer\dpclat_x64.sys
```

当前目录的再分发状态为 `not-authorized`，安全状态为 `not-approved`。`stage-private` 在任何复制前拒绝；没有强制跳过参数。未来只有取得实际书面许可、完成驱动安全及目标环境核验后，才能审查修改目录，绑定私有授权原件的 SHA，再从外部输入复制到独立成品布局的 `Drivers/DpcTimer/dpclat_x64.sys`。公开 JSON 中写入状态本身不创造分发许可。

```powershell
# 当前会明确失败；授权/安全审查未完成，不会复制任何驱动。
python tools/driver_payloads.py stage-private --input D:\PrivateBuildInputs\DpcTimer\dpclat_x64.sys --layout D:\BuildOutput\DeltaNFD --authorization-evidence D:\PrivateBuildInputs\permission.pdf
```

`installer/publish-selfcontained.ps1` 完成布局后调用 `audit-layout`；现阶段若出现 DPC 字节或专用目录则报错。手工修改布局后，应在调用 Inno Setup 前再次审计，直接执行 ISCC 不会替代该检查：

```powershell
python tools/driver_payloads.py audit-layout --layout D:\BuildOutput\DeltaNFD
```

未来获批的安装包可包含独立第三方文件，公开源码仍不包含该文件；对应原厂条款和声明必须随实际分发保留。已有签名不证明 Win10 内核必然允许加载，也不证明游戏反作弊兼容。
