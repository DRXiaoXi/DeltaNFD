# 三角帧不掉洲 / Delta NFD

Windows 10/11 x64 游戏优化工具，基于 .NET 8 / WinUI 3。当前公测版本 **Beta0.93-RC2 / 0.9304**。

[Windows x64 安装包](https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.9304/DeltaNFD_Setup_0.9304_x64.exe) · [国内备用下载](https://deltaxiaoxi.xin/updates/packages/0.9304/DeltaNFD_Setup_0.9304_x64.exe) · [完整发布说明](https://github.com/DRXiaoXi/DeltaNFD/releases/tag/v0.9304) · [源码 ZIP](https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.9304/DeltaNFD_Source_0.9304.zip)

安装包 297558459 字节，SHA256：`78c91e871430fbf8a9f0dbb04a958d23be29563960fe4c78a47cac894e487ec2`。JSON 清单和 gzip 对象供自动更新使用，普通用户下载 EXE 即可。

## 本次变化

保留 14.42 默认 / 14.44 可选双基线；确认 NVIDIA 596 系列驱动时隐藏 14.44 并回到 14.42。包含 51 个正式基线 DLL 复检、明确缺失后的受控 MSI 恢复、页面会话修复、游戏状态只读兜底、帧格节能参数兼容和 HAGS 布局/刷新意图修复。强制替换继续通过原预览、备份和高风险确认执行。

保留系统优化、显卡型号伪装与恢复、NVIDIA 设置、着色器缓存维护、ACE 检测及受控清理、帧格、CPU 实验室、进程管理、手动日志反馈。默认自建更新服务器和替换变更文件，保留用户选择与整包回退。

本公开源码及安装包只包含六个维护后端，不含卡顿检测、PresentMon 研究、独立预热客户端或私有材料。

## 获取源码与构建

Windows x64 需要 .NET 8 SDK、Python 3.11+，生成安装器还需 Inno Setup 6。建议使用较短工作目录。

```powershell
git clone https://github.com/DRXiaoXi/DeltaNFD.git
cd DeltaNFD
# 复现本版本时，发布后切换到对应标签。
git switch --detach v0.9304
python tools/prepare-source-assets.py
dotnet build src/DeltaNFD/DeltaNFD.csproj -c Release -p:Platform=x64
installer/make-installer.cmd
```

资源引导只下载并校验固定的 0.9200 原始媒体和 0.9299 官方 14.44 媒体，不执行运行库安装器。错误的已有输入会保留并报错。源码 ZIP 的 `SOURCE_FILES_SHA256.json` 记录完整受控输入，`SOURCE_BUILD.md` 记录构建边界；程序集的 Git 后缀本身不能替代源码身份核对。

验证以隔离、模拟和只读检查为主，不代表真实游戏或系统运行库重装已经实机验收。已发布版本及旧更新对象保留，不覆盖同号字节。

原创代码采用 GPL-3.0-only，第三方许可见 [LICENSE](LICENSE)、[COPYRIGHT](COPYRIGHT)、[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
