# Alpha0.931-RC5 / 0.9318

仅自建内测通道发布。保留RC4功能范围，修复SysMain固定读取遗漏，低内存引导加入独立禁用预读取与按备份恢复开关；N卡设置加入手动“防闪屏性重置”，完整重置三角洲专用驱动覆盖前先备份。不会自动检测闪屏或清理缓存，不保证解决所有闪屏。

钟摆计划与忽略精度请求仍沿用当前隐藏范围，默认不提升游戏优先级。排除运行库研究、退役卡顿检测及私有反馈。验证以隔离夹具、模拟写入和只读检查为主，未实测游戏修复效果或真实安装升级。

使用.NET SDK8.0.425、Python3.11+，安装器需Inno Setup6。先运行 `python tools/prepare-source-assets.py` 校验并恢复固定厂商依赖，再运行 `installer/publish-selfcontained.ps1`，用 `installer/DeltaNFD.iss` 打包。原始update.json仅历史样例；完整受控构建输入保留本地。

原创代码GPL-3.0-only，第三方许可见LICENSE、COPYRIGHT、THIRD-PARTY-NOTICES.md。本地版本源码及Git身份不表示已上传GitHub。
