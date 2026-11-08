# Beta0.931-RC3 / 0.9316

本标签为 RC3 的对应产品源码与固定构建输入。包含运行库判定回退、621+ NVPH 放宽、默认三角洲 ACE 检测、亮度修复、后端中断核对、录制记录核对和独立 ReBAR v2。

621.xx 新旧缓存布局分别提供绿色 ASD已激活、黄色未激活ASD提示，后者无需去处理；该状态按缓存布局推断。

运行库研究策略、退役卡顿检测和私有反馈材料不属于本版本。使用 .NET SDK 8.0.425，先执行 `python tools/prepare-source-assets.py` 校验并恢复未入库的厂商构建依赖（仅下载与哈希校验，不运行安装器），再执行 `installer/publish-selfcontained.ps1` 生成自包含布局；安装包由 `installer/DeltaNFD.iss` 生成。根 update.json 是历史发布样例，实际更新清单由发布工具生成并部署。

[Windows x64 安装包](https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.9316/DeltaNFD_Setup_0.9316_x64.exe) · [国内备用下载](https://deltaxiaoxi.xin/updates/packages/0.9316/DeltaNFD_Setup_0.9316_x64.exe) · [发布说明与对应源码](https://github.com/DRXiaoXi/DeltaNFD/releases/tag/v0.9316)

## 获取源码与构建

Windows x64 需要 .NET 8 SDK、Python 3.11+，生成安装器还需 Inno Setup 6。建议使用较短工作目录。

```powershell
git clone https://github.com/DRXiaoXi/DeltaNFD.git
cd DeltaNFD
# 复现本版本时，发布后切换到对应标签。
git switch --detach v0.9316
python tools/prepare-source-assets.py
dotnet build src/DeltaNFD/DeltaNFD.csproj -c Release -p:Platform=x64
installer/make-installer.cmd
```

资源引导只下载并校验固定的 0.9200 原始媒体和 0.9299 官方 14.44 媒体，不执行运行库安装器。错误的已有输入会保留并报错。源码 ZIP 的 `SOURCE_FILES_SHA256.json` 记录完整受控输入，`SOURCE_BUILD.md` 记录构建边界；程序集的 Git 后缀本身不能替代源码身份核对。

验证以隔离、模拟和只读检查为主，不代表真实游戏或系统运行库重装已经实机验收。已发布版本及旧更新对象保留，不覆盖同号字节。

原创代码采用 GPL-3.0-only，第三方许可见 [LICENSE](LICENSE)、[COPYRIGHT](COPYRIGHT)、[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
