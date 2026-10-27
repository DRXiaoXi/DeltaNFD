# 三角帧不掉洲 / Delta NFD

Windows 10/11 x64 游戏优化工具，使用 .NET 8 与 WinUI 3。原创代码采用 GPL-3.0-only；第三方内容保留原许可，见 [LICENSE](LICENSE) 与 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 下载与版本

- 已发布公测版：[Beta0.922-RC3 / v0.9223](https://github.com/DRXiaoXi/DeltaNFD/releases/tag/v0.9223)。显示版本为 Beta0.922-RC3，数值版本使用 0.9223，以兼容旧客户端的整数版本排序。
- [GitHub 完整安装包](https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.9223/DeltaNFD_Setup_0.9223_x64.exe) / [自建服务器完整安装包](https://deltaxiaoxi.xin/updates/packages/0.9223/DeltaNFD_Setup_0.9223_x64.exe)。大小 250103578 字节，SHA256：`79edf6f2cae22a9a7db009c51ab788b56b3e5fcd8b3b29c8901beeb036853440`。
- 对应安装包的源码：[v0.9223 标签](https://github.com/DRXiaoXi/DeltaNFD/tree/v0.9223) / [发布源码 ZIP](https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.9223/DeltaNFD_Source_0.9223.zip)。ZIP 的 `SOURCE_FILES_SHA256.json` 记录完整构建源码输入。
- **main 与当前发布源码同步**；复现某次发布时使用对应标签或源码 ZIP，后续维护变化不会覆盖旧版本包。

### 旧版本检查更新提示“清单体积异常”

Beta0.92 的文件更新对象使 GitHub Releases API 返回超过旧客户端的 262144 字符限制。这是客户端与发布布局的兼容问题。旧程序内置的限制不能由远端清单修改，请完全退出旧程序后，手动下载上方完整安装包覆盖安装。Beta0.92 安装包默认使用自建服务器与仅替换变更文件更新；已经保存的服务器选择会保留，必要时在设置中手动切换自建服务器。

GitHub 发布列表现使用独立的有界读取：每次响应最多 8 MiB，累计分页最多 16 Mi 字符，普通清单继续保持小体积限制。修正已包含在 Beta0.922-RC3 安装包。

## 当前功能范围

- 系统优化、显卡名称伪装与恢复、NVIDIA 驱动配置、着色器缓存修复指引、ACE 检测及受控清理。
- 帧格一键配置、CPU 实验室、进程管理和三角洲规则；核心常驻已移除，游戏进程禁电源限制作为加速系统响应子项保留，默认关闭。
- C++ 14.x 兼容提醒、单独修复和全量修复、运行库防护、失败诊断及手动处理指引。可以关闭运行库检测；颜色是项目兼容策略，不代表已证明损坏或掉帧。
- 手动 BUG 反馈：玩家填写、预览并同意后才上传日志与描述；失败时显示原因。
- 更新默认自建服务器、仅替换变更文件，可选择 GitHub、自动模式或完整安装包；文件更新失败且满足恢复条件时自动回退整包。设置、备份和日志保留。

旧着色器卡顿检测实现已撤下，后续将重新设计；着色器缓存维护功能保留。

## 获取源码与构建

```powershell
git clone https://github.com/DRXiaoXi/DeltaNFD.git
cd DeltaNFD
# 复现当前已发布代码时切换到 v0.9220；维护 main 时无需切换。
# git switch --detach v0.9220
```

建议克隆到较短路径，避免旧 VSTO 资源触及 Windows MAX_PATH。需要 Windows x64、.NET 8 SDK、Python 3.11+；完整安装包另需 Inno Setup 6。固定的二进制输入未全部放进 Git，维护 main 提供只下载和校验、不执行安装器的准备脚本：

```powershell
python tools/prepare-source-assets.py
dotnet build src/DeltaNFD/DeltaNFD.csproj -c Release -p:Platform=x64
dotnet build tools/BackendSmokeTest/BackendSmokeTest.csproj -c Release

dotnet run --project tools/BackendSmokeTest -c Release --no-build -- --update-response-checks
dotnet run --project tools/BackendSmokeTest -c Release --no-build -- --update-source-fallback-checks
dotnet run --project tools/BackendSmokeTest -c Release --no-build -- --update-channel-checks
```

历史标签中没有新增的准备脚本。构建 v0.9200 时，可先在维护 main 准备资源，再保留被 Git 忽略的资源切换标签；使用源码 ZIP 则按其中的 `SOURCE_BUILD.md` 和固定资源清单操作。

完整发布布局使用 `installer/publish-selfcontained.ps1`，EXE 安装包使用 `installer/make-installer.cmd`。资源准备、可复现边界和发布维护说明见 [构建与更新维护](notes/Beta092源码构建与更新维护-20261017.md)。第三方原包不改为 GPL；本项目不自动执行资源准备脚本下载的运行库。

## 开发与发布约定

- 参与开发见 [贡献指南](CONTRIBUTING.md)，目录职责与安全清理见 [工作区维护](docs/WORKSPACE-MAINTENANCE.md)，安全问题见 [反馈说明](SECURITY.md)。提交前运行 `python tools/check_repository.py --include-untracked`。
- main 和 `codex/**` 分支、`v*` 标签及 Pull Request 运行构建检查；CI 与本地检查不能代替真实游戏、弱 CPU、安全模式或运行库修复实测。
- [文件更新发布说明](installer/FILE-UPDATES.md) 包含整包回退、服务器选择、上传顺序和恢复要求。
- 已发布版本目录、安装包、源码 ZIP、文件清单及对象保持不可变。产品变更重新发布时必须使用更高的数值版本。
- 不上传真实玩家日志、私钥、令牌或用户配置。反馈服务代码位于 `server/feedback`，接收数据不放进公开仓库。

## 致谢与许可

感谢项目贡献者，以及 Henry++ / Mem Reduct、AtlasOS、Microsoft、Intel / PresentMon、abbodi1406 等上游项目。使用范围和各自许可见 [第三方声明](THIRD-PARTY-NOTICES.md)。
