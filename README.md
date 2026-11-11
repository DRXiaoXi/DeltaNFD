# Beta0.932-RC2 / 0.9321

公测版可选更新，自建公测、内测双通道及 GitHub 同步安装包和对应源码。

- 修复本地发布缺少 WinUI 启动资源造成的黑屏或启动失败，构建时校验资源完整性。
- 改进 Windows 注销时 CPU/帧格的收尾与还原；未完成仍保留核对保护。
- 修复 CPU 断连记录被只读检测覆盖的问题，完善证据落盘、子进程身份核对，并为损坏运行库外层记录、失效旧电源计划备份提供显式处理入口。
- 新增本地 MCP Agent 接入，正常启动默认开启；Agent 写入仍需要软件内确认，并保留管理员、游戏运行、备份和核对限制。
- 检测到内部版本更高的新 Beta 时，左上角版本号后提示“过时版本”。
- 包含 RC1 的预读取复检、NVIDIA 防闪屏性重置枚举修复与未知后端异常处理指引。

钟摆计划和忽略三角洲精度请求继续隐藏，默认不提升游戏优先级。不含退役卡顿检测、运行库研究策略或私有反馈。更新不强制，不自动重启。

本次验证以隔离夹具、模拟写入及 Win11 只读检查为主，未验证 Win10、跨 UAC、真实游戏画面或系统优化实效。历史断连的原始会话不全，不能认定所有断连根因均已解决。

源码构建使用 .NET SDK 8.0.425、Python 3.11+，安装器需 Inno Setup 6。先运行 `python tools/prepare-source-assets.py` 校验并恢复固定厂商依赖，再运行 `installer/publish-selfcontained.ps1`，以 `installer/DeltaNFD.iss` 打包。详见 SOURCE_BUILD.md；Agent 接入见 docs/MCP本地Agent接入.md。

原创代码 GPL-3.0-only，第三方许可见 LICENSE、COPYRIGHT、THIRD-PARTY-NOTICES.md。GitHub 标签 v0.9321 对应本次产品源码。
