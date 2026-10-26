# 第三方内容与历史授权

自 Alpha0.915 起，Delta NFD 原创代码采用 GPL-3.0-only。本次授权不对第三方材料重新授权，各组件及独立资源继续适用原许可证和声明。

- **Henry++ / Mem Reduct（设计参考）**：https://github.com/henrypp/memreduct 。感谢其内存压力触发、冷却及分类清理的设计参考。新版逐进程内存回收依据 Windows 接口独立实现，未复制、移植或集成 Mem Reduct / routine 的 GPL 源码或二进制；上游原始内容适用 GPLv3，本项目许可证不对其重新授权。

- **历史 Delta NFD 发布版**：历史版本已授予的 GPL 或其他授权继续适用，不追溯收回既有权利。当前 GPLv3 全文见 `LICENSE` 与 `licenses/GPL-3.0.txt`。
- **AtlasOS 电源计划**：`Assets/NoPowerSave-Scheme.pow` 基于 AtlasOS 导出的电源计划，使用时改名。该独立资源保留上游授权与署名。上游：https://github.com/Atlas-OS/Atlas 。相关 GPL-3.0 全文随附，感谢 AtlasOS 及其贡献者。
- **abbodi1406 / VisualCppRedist AIO（历史版本）**：历史运行库整合资源与 `Installer.cmd` 来自 https://github.com/abbodi1406/vcredist ，保留原作者及上游贡献者声明。当前定向修复不再集成 AIO；项目许可证不重新授权历史脚本或资源。
- **Microsoft Visual C++ / DirectX**：Beta0.919 的 C++ 14.x 全量重装使用固定 2015–2022 14.42.34433 x64/x86 微软原包；安装包、MSI、CAB、DLL 与相关材料保留原始条款，不改为 GPLv3 授权。历史 DirectX 资源来自微软 June 2010 redistributable，当前不再分发。
- **.NET、Windows App SDK、Windows SDK、CommunityToolkit.Mvvm**：保留各自上游许可证及自包含运行时附带的声明。为避免覆盖运行时文件，项目许可证在安装目录命名为 `DELTA-NFD-LICENSE.txt`。
- **Microsoft .NET 8 x64 离线修复安装器（历史版本）**：历史版本曾携带微软原版 Runtime 8.0.31 安装器，其内容沿用微软原始条款。当前已移除离线修复安装器，自包含 .NET 运行时及其声明继续保留。
- **第三方名称、商标和独立素材**：不因被本项目使用而转移权利；已有独立声明优先适用。

系统设置事实、Windows 接口名称及其他不受著作权专有控制的内容，不因项目许可证产生额外排他权。第三方内容的遗漏可通过原项目仓库反馈补充，本清单不构成对未核实权利的额外授权。
