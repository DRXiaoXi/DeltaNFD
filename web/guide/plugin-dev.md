# 插件开发与声明式 UI 指南

DeltaNFD 提供了完善的插件系统（`PluginManagerService` + `PluginRuntimeService`），支持开发者通过简单的 `manifest.json` 和声明式 `ui.json` 扩展专属调优功能（可参考仓库中的 `src/Example.Plugin.HardwareReport` 与 `src/Plugin.WeakNetBoost`）。

## 一、 插件包目录结构

一个标准的 DeltaNFD 插件包包含以下文件：

```text
MyPlugin/
├── package/
│   ├── manifest.json     # 插件元数据、版本与入口定义
│   ├── ui.json           # 声明式 UI 界面布局与操作绑定
│   ├── README.md         # 插件说明文档
│   └── LICENSE.txt       # 开源或分发许可
└── Program.cs            # 插件后端执行逻辑（通过标准 IPC 与主程序通信）
