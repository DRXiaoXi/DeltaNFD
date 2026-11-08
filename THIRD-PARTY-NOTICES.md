# 第三方内容与历史授权

自 Alpha0.915 起，Delta NFD 原创代码采用 GPL-3.0-only。本次授权不对第三方材料重新授权，各组件及独立资源继续适用原许可证和声明。

- **Henry++ / Mem Reduct（设计参考）**：https://github.com/henrypp/memreduct 。感谢其内存压力触发、冷却及分类清理的设计参考。新版逐进程内存回收依据 Windows 接口独立实现，未复制、移植或集成 Mem Reduct / routine 的 GPL 源码或二进制；上游原始内容适用 GPLv3，本项目许可证不对其重新授权。

- **历史 Delta NFD 发布版**：历史版本已授予的 GPL 或其他授权继续适用，不追溯收回既有权利。当前 GPLv3 全文见 `LICENSE` 与 `licenses/GPL-3.0.txt`。
- **AtlasOS 电源计划**：`Assets/NoPowerSave-Scheme.pow` 基于 AtlasOS 导出的电源计划，使用时改名。该独立资源保留上游授权与署名。上游：https://github.com/Atlas-OS/Atlas 。相关 GPL-3.0 全文随附，感谢 AtlasOS 及其贡献者。
- **abbodi1406 / VisualCppRedist AIO（历史兼容材料）**：旧版全量修复曾调用筛选后的 `Installer.cmd`。当前“完整重装 C++ 运行库”只使用微软官方 C++ 原包，不再执行 AIO、VSTO 或旧 VB 辅助安装。历史材料保留上游声明；感谢 abbodi1406、@ricktendo64、@burfadel 及贡献者。上游仓库声明 Unlicense；本项目不重新授权第三方内容。
- **Microsoft Visual C++ / DirectX**：当前 C++ 安装基线仅 14.44.35211 x64/x86，并保留官方 2005–2013 原包。安装包、MSI、CAB、DLL 与相关材料沿用微软原始条款，不改为 GPLv3 授权。2026-11-02 本地非 C++ 原型静态参考用户提供的 DirectX Repair 附件清单，仅复制 182 个签名有效的微软 DLL，不复制或调用第三方 EXE、代码、设置与日志。4 个 Windows 维护组件仅诊断；C++、UCRT/APISet 不由该模式覆盖。附件缺少可核实的完整再分发许可，正式打包或公开发布前必须另外核对微软原始分发条款；本地核验记录不构成公开分发授权。
- **.NET、Windows App SDK、Windows SDK、CommunityToolkit.Mvvm**：保留各自上游许可证及自包含运行时附带的声明。为避免覆盖运行时文件，项目许可证在安装目录命名为 `DELTA-NFD-LICENSE.txt`。
- **Microsoft .NET 8 x64 离线修复安装器（历史版本）**：历史版本曾携带微软原版 Runtime 8.0.31 安装器，其内容沿用微软原始条款。当前已移除离线修复安装器，自包含 .NET 运行时及其声明继续保留。
- **Intel / PresentMon 2.6.0（本地稳帧研究工具）**：可选的只读帧事件采集器来自 https://github.com/GameTechDev/PresentMon ，按 MIT 许可证使用，声明保留于 `tools/FramePacingResearch/PresentMon-LICENSE.txt`。当前仅下载到忽略的本地研究目录，不纳入主程序或安装包；保留其 MIT 授权。
- **第三方名称、商标和独立素材**：不因被本项目使用而转移权利；已有独立声明优先适用。

系统设置事实、Windows 接口名称及其他不受著作权专有控制的内容，不因项目许可证产生额外排他权。第三方内容的遗漏可通过原项目仓库反馈补充，本清单不构成对未核实权利的额外授权。
