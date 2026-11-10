# Beta0.932-RC1 / 0.9320

公测版可选更新，自建公测、内测双通道及 GitHub 同步安装包与对应源码。

- 修复低内存引导与系统优化页预读取状态复检路径不一致，统一实际读取与执行定义；读取失败继续显示未知。
- 修复部分 NVIDIA 驱动在“防闪屏性重置”时因设置枚举重复而拒绝操作的问题，保留原始备份、配置归属门禁和重置后复检。
- 未知后端异常现在按进程状态、备份和恢复范围提供处理指引，并支持预览、复制脱敏诊断摘要。不会自动清除待核对记录或重放操作。
- 包含 Alpha RC5 的 SysMain 检测修复、独立预读取开关和手动防闪屏性重置入口。

钟摆计划、忽略三角洲精度请求继续隐藏；默认不提升游戏优先级。不含退役卡顿检测与运行库研究策略。

防闪屏重置会清除三角洲专用驱动配置中的 DLSS、ReBAR 等覆盖，操作前先备份；不保证解决全部闪屏。验收主要使用隔离夹具、模拟写入及 Win11 只读检查，不代表 Win10 或真实游戏画面效果已验证。更新不强制，不自动重启电脑。

源码构建使用 .NET SDK 8.0.425、Python 3.11+，安装器需 Inno Setup 6。先运行 `python tools/prepare-source-assets.py` 校验并恢复固定厂商依赖，再运行 `installer/publish-selfcontained.ps1`，以 `installer/DeltaNFD.iss` 打包。构建细节见 SOURCE_BUILD.md；根 update.json 是历史样例。

原创代码 GPL-3.0-only，第三方许可见 LICENSE、COPYRIGHT、THIRD-PARTY-NOTICES.md。GitHub 标签 v0.9320 对应本次产品源码。
