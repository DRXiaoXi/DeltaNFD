# 三角帧不掉洲 / DeltaNFD

Alpha0.929-RC2（内部版本 0.9292），内测通道。

[下载安装包](https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.9292/DeltaNFD_Setup_0.9292_x64.exe) · [发布页面](https://github.com/DRXiaoXi/DeltaNFD/releases/tag/v0.9292)

修复双 CCD 调度重复读取带来的延迟、鼠标加速即时生效与 HAGS 状态判定、ACE 多游戏共存误报，以及强制替换 14.x 载荷路径。保留 RC1 安装交接编码修复与六个维护后端。

不包含卡顿检测、着色器研究、独立预热客户端或私有密钥。系统缓存维护保留，旧恢复记录兼容身份保留。

Windows x64，.NET 8。构建前运行 `python tools/prepare-source-assets.py` 获取固定且逐项核验的既有公开资源，再运行 `installer/make-installer.cmd`。

仅内测预发布，公测版本不变。软件按 GPL-3.0-only 分发，第三方许可见对应声明文件。
