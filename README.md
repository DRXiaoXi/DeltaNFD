# 三角帧不掉洲 / DeltaNFD

Alpha0.929（内部版本 0.9290），内测通道。

[下载安装包](https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.9290/DeltaNFD_Setup_0.9290_x64.exe) · [发布页面](https://github.com/DRXiaoXi/DeltaNFD/releases/tag/v0.9290)

本次保留系统与缓存维护、运行库、帧格、进程管理等功能。栏目后端按需运行、启动优化状态预读；移除后台抑制，增加 ACE 进程保护、Intel 首选 P 核和帧格关闭修复。

本源码和安装包不包含卡顿检测、着色器研究、独立预热客户端或私有密钥。旧恢复记录的兼容身份仍保留。

Windows x64，.NET 8。缺少二进制构建资源时先运行 `python tools/prepare-source-assets.py`，再运行 `installer/make-installer.cmd`。资源固定为已发布的 0.9200 基线并逐项校验。

此版本为内测预发布，公测版本保持不变。软件按 GPL-3.0-only 分发，第三方许可见对应声明文件。
