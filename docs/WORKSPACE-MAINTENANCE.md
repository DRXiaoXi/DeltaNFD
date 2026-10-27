# 工作区与 GitHub 维护

源码、开发输出和发布归档各有不同保留要求。`.gitignore` 只决定是否被 Git 默认发现，不能作为删除授权。不要直接使用 `git clean -xfd` 清空本项目：未提交源码、研究材料、运行库原包和发布证据都可能丢失。

## 目录职责

| 路径 | 内容 | 维护方式 |
| --- | --- | --- |
| `src/`、`tools/` | 产品和开发工具源码 | 入库；其中 `bin/obj` 可重建 |
| `installer/`、`server/` | 发布脚本与服务端源码 | 入库；生成目录与私密配置不入库 |
| `notes/`、`docs/` | 开发验收记录、维护文档 | 保留；不能把历史结论当作当前实现 |
| `retired/` | 已退休的源码与固定依赖 | 保留原字节，不参与现行构建 |
| `_buildcheck/` | 隔离构建、测试、源码投影、发布记录 | 不入库；按清单清理生成输出 |
| `artifacts/`、`research/`、`.tmp_logs*/` | 本地分析、研究与日志证据 | 不入库；默认保留，避免误删唯一材料 |
| `installer/Output`、`installer/Backup` | 生成的安装包和历史备份 | 不入库；按明确的版本保留清单清理，交接文档另行保留 |

## 安全清理

需要 PowerShell 7。退出从构建目录启动的程序，停止构建、调试、测试和安装器后，先生成只读预览：

```powershell
pwsh -NoProfile -File tools/Clean-Workspace.ps1
```

命令把计划写到 `_buildcheck/workspace-cleanup-<时间>/cleanup-plan.json`，不删除文件。查看其中每一个目标，先备份需要的运行日志、配置和恢复证据，然后应用该份计划：

```powershell
pwsh -NoProfile -File tools/Clean-Workspace.ps1 -PlanPath <计划文件>
pwsh -NoProfile -File tools/Clean-Workspace.ps1 -Apply -PlanPath <计划文件>
```

也支持 `-WhatIf`。实际删除限定为本仓库中的 `bin/obj`、识别出的自包含布局和临时组件目录；拒绝路径越界、根或祖先链接、目录内链接、嵌套仓库，以及包含 Git 可见源码的目标。删除前先预检所有目标；失败逐项记录，不报告为全部成功。清理不提供回收站或自动回滚。

固定保护包含 Beta0.922 的 `publish/source/files/package`、当前 `Assets`、历史安装包和研究证据。新增冻结版本时，应同步更新脚本的 `$protected` 清单；不能假设脚本自动知道哪个本地构建已经发布。

### 2026-10-26 本机保留策略

最新最小归档为 `_buildcheck/Beta0.93/package` 的 5 个文件，约 343 MB：安装包、精确源码 ZIP、两份对应校验文件和 SHA256SUMS。旧 Alpha/RC 安装包、展开副本、更新对象及可重建输出按本轮明确清单清理；失败日志、发布记录和必要证据保留。通用清理工具的默认保护不放宽。

缓存格式、PSO、驱动分析和原始样本外置到 `D:\我要干大活\三角洲着色器缓存研究`，逐文件哈希核验后移除主项目原件，入口见 [研究档案索引](SHADER-RESEARCH-ARCHIVE.md)。归档包含内嵌参考仓库的 Git 历史，保持本地私有，不进入产品源码 ZIP、反馈附件或自动上传。`_analysis` 同样作为本地分析目录忽略。

用户已明确删除着色卡顿杀手独立项目、旧研究原型和旧集成归档；不为这些项目制作完整备份。主程序的缓存维护功能、开发卡顿检测工程与旧任务核对逻辑继续保留，不按名称全局删除。

本轮清单和验证结果在 `_buildcheck/workspace-cleanup-20261026/`。不终止用户程序，不删除 AppData、真实安装目录、服务器备份或线上发布；也不重新执行历史关机请求。

2026-10-19 用户另行明确授权第二轮清理，本机发布产物改为只保留 `_buildcheck/Beta0.922/package` 中的 6 个最小归档文件：安装包、精确源码 ZIP、两份对应校验文件、SHA256SUMS 和构建报告，共约 294 MB。旧安装包、旧源码归档、更新对象和展开的发布副本可以删除；当前源码、Assets、研究/诊断证据和此前恢复归档继续保留。GitHub 与自建服务器的发布不变。

这次授权通过 `_buildcheck/workspace-release-cleanup-20261019/cleanup-plan.json` 单独执行，不放宽通用工具的默认保护。默认保护中的目录不存在时不会自动重建；以后清理发布归档仍须明确列出保留版本与文件，不能仅按扩展名删除。旧备份目录中的 HANDOFF 文档保留，嵌套仓库和用途不明的原始输入跳过并记录。

清理报告写在计划旁的 `cleanup-result.jsonl`。重复应用已完成的计划会跳过消失的目录。不要在清理过程中启动会向这些目录写文件的程序。

## 控制增长与构建污染

- 每个主题使用一个隔离的 SDK artifacts 目录。发布脚本要求新的输出目录，保持此约束；不要向旧发布目录覆盖复制。
- 一个里程碑验收完成后，保留指定版本的最小发布归档、哈希、清单、日志及必要夹具，清理该轮可重建的中间输出；不自动累积所有旧安装包。
- 可再下载的资源缓存和唯一的原包分开处理。`tools/prepare-source-assets.py` 校验固定来源与哈希，不执行下载的安装器；当前原始 `Assets` 不参加自动清理。
- 大型发布物放到 GitHub Releases 或受控的发布存储，不复制进源码目录。Git 历史体积与工作区体积分别核对；本轮膨胀主要来自本地构建，不需要重写历史。

## GitHub 提交检查

```powershell
python tools/check_repository.py --include-untracked
python -m unittest discover -s tools -p test_repository_maintenance.py -v
git diff --check
```

仓库检查拒绝生成目录、常见凭据文件、诊断转储和达到 100 MiB 的文件，对 50–100 MiB 文件提示复核；参考 [GitHub 大文件说明](https://docs.github.com/en/repositories/working-with-files/managing-large-files/about-large-files-on-github)。它检查 Git 可见工作文件；不扫描全部忽略目录，也不代替内容层面的密钥扫描。GitHub CI 对检出的提交运行同样检查和隔离的清理工具测试。

`.editorconfig` 和 `.gitattributes` 统一后续编辑与入库换行，不批量重写当前源码。固定哈希的脚本、退休档案和 vendor 输入保持原字节。Issue/PR 模板要求脱敏证据和具体的验证范围。

当前开发工作区包含未发布更改和 P2 卡顿检测，不能代替 Beta0.922 的公开源码投影。规范化文件不构成发布授权，不应顺带提交全部功能变化、推送标签或覆盖发布附件。
