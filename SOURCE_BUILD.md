# Beta0.939-RC3 / 0.9393 构建输入

## 第三方驱动源码与载荷边界（2026-11-14）

GitHub 源码不携带 DPC 的 SYS、原始 EXE、私有授权或其压缩副本；公开校验目录和构建工具不等于第三方授权。当前 DPC 目录明确 `not-authorized` / `not-approved`，不会恢复下载、嵌入或发布该驱动。源码、Git 索引和成品布局的边界检查及后续构建输入方法见 [DRIVER_PAYLOADS.md](DRIVER_PAYLOADS.md)。当前产品接入方式未切换，既有 Alpha r3 包也未改变。

本标签对应 Beta0.939-RC3 的公测源码和预发布。使用 Windows x64、.NET SDK 8.0.425、Python 3.11+；安装器另需 Inno Setup 6。

先运行 `python tools/prepare-source-assets.py` 校验并恢复固定厂商依赖，再运行 `installer/publish-selfcontained.ps1` 生成自包含布局，最后使用 `installer/DeltaNFD.iss` 生成安装器。引导只下载并核验资源，不运行厂商安装器。固定清单包括 0.9200 原始资源、0.9299 官方14.44资源及0.9315 DirectX资源，详见引导脚本及其固定哈希。

`SOURCE_FILES_SHA256.json` 按 Git 对象内容记录公开源文件 SHA256（不包含自身），Git checkout 的换行转换不改变源对象身份。未入库的二进制依赖按固定资源清单恢复；完整构建输入和验收记录另在本地保留。不承诺安装器逐字节可重现。

14.52.37029 两个固定 SHA 的微软原包附在 GitHub v0.9392（Beta0.939-RC2）发布中，名称为 vc_redist.14.52.37029.x86.exe / vc_redist.14.52.37029.x64.exe。下载原包并安装 WiX Toolset 5 后，运行 `python tools/prepare-vc1452-media.py --x86 <x86原包完整路径> --x64 <x64原包完整路径>`。脚本先核验两份固定 SHA256，再静态提取并核验四套 MSI/CAB，恢复 10 个构建输入；不执行原包或修改系统运行库。已有不同内容的目标文件会保留并报错，不覆盖。安装包与 MSI/CAB 沿用既有二进制依赖策略，不进入 Git 源码；固定身份和哈希清单随源码保留。

产品为六维护后端、自包含.NET8布局。排除运行库研究策略、已退役卡顿检测、反馈内容、凭据和本地日志。根 update.json 为历史样例，不是实际线上清单。验证以隔离夹具、模拟写入及只读检查为主，不代表实际游戏、真实维护或安装升级已验收。

本次另包含同用户本地 MCP stdio 宿主及 AgentBridge；官方维护后端仍为六个。
