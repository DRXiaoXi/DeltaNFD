# Beta0.931-RC4 / 0.9317

本版本保留 RC3 的全部修复，新增 UP 推荐首栏与 HAGS 去重、低内存配置引导，以及显卡型号实际修改后的本次着色器维护提醒。提醒仅存于当前进程，可随时关闭，不自动清理缓存。

旧驱动不再接受双精确256MiB缓存；621.xx 有效布局包含两份精确256MiB时显示绿色“ASD已激活”，其他双大旧布局仍黄色“未激活ASD”，不显示“去处理”。ASD仅按缓存布局推断，不证明驱动开关或性能。

公测版继续隐藏钟摆计划与帧格忽略精度请求，默认不提升三角洲优先级。

运行库研究策略、退役卡顿检测和私有反馈材料不属于本版本。使用 .NET SDK 8.0.425，先执行 `python tools/prepare-source-assets.py` 校验并恢复未入库的厂商构建依赖（仅下载与哈希校验，不运行安装器），再执行 `installer/publish-selfcontained.ps1` 生成自包含布局；安装包由 `installer/DeltaNFD.iss` 生成。根 update.json 是历史发布样例，实际更新清单由发布工具生成并部署。

[Windows x64 安装包](https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.9317/DeltaNFD_Setup_0.9317_x64.exe) · [国内备用下载](https://deltaxiaoxi.xin/updates/packages/0.9317/DeltaNFD_Setup_0.9317_x64.exe) · [发布说明与对应源码](https://github.com/DRXiaoXi/DeltaNFD/releases/tag/v0.9317)

## 获取源码与构建

Windows x64 需要 .NET 8 SDK、Python 3.11+，生成安装器还需 Inno Setup 6。建议使用较短工作目录。

```powershell
git clone https://github.com/DRXiaoXi/DeltaNFD.git
cd DeltaNFD
# 复现本版本时，发布后切换到对应标签。
git switch --detach v0.9317
python tools/prepare-source-assets.py
dotnet build src/DeltaNFD/DeltaNFD.csproj -c Release -p:Platform=x64
installer/make-installer.cmd
```

资源引导只下载并校验固定的 0.9200 原始媒体和 0.9299 官方 14.44 媒体，不执行运行库安装器。错误的已有输入会保留并报错。源码 ZIP 的 `SOURCE_FILES_SHA256.json` 记录完整受控输入，`SOURCE_BUILD.md` 记录构建边界；程序集的 Git 后缀本身不能替代源码身份核对。

验证以隔离、模拟和只读检查为主，不代表真实游戏或系统运行库重装已经实机验收。已发布版本及旧更新对象保留，不覆盖同号字节。

原创代码采用 GPL-3.0-only，第三方许可见 [LICENSE](LICENSE)、[COPYRIGHT](COPYRIGHT)、[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
