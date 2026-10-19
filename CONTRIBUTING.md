# 参与开发

项目使用 GPL-3.0-only，第三方资源按各自许可分发。提交前请阅读 [README](README.md)、[第三方声明](THIRD-PARTY-NOTICES.md) 和 [AGENTS.md](AGENTS.md) 中最新约定。

## 工作目录与分支

- `src/`：产品源码；`tools/`：开发与验收工具；`installer/`：安装和发布脚本；`server/`：服务端源码。
- `notes/`：实现与验收记录；`retired/`：已退休实现的参考档案，不参与现行构建。
- `_buildcheck/`：隔离构建、测试夹具和发布记录；`artifacts/`、`research/`：本地诊断与研究。它们不进入 Git。
- 从目标分支创建 `codex/<主题>` 分支。一项修改一个主题；不要把未发布功能混入发布维护提交。
- 已有工作区可能包含未提交工作。不要运行 `git reset --hard`、全目录 `git clean` 或批量暂存无关文件。

## 构建与验证

Windows x64、.NET 8 SDK 与 Python 3.11+ 的准备步骤见 [README](README.md#获取源码与构建)。先恢复并核验固定资源，再构建；不要通过复制旧 `bin/obj/publish` 拼装发布目录。

```powershell
python tools/check_repository.py --include-untracked
git diff --check
dotnet build src/DeltaNFD/DeltaNFD.csproj -c Release -p:Platform=x64
dotnet build tools/BackendSmokeTest/BackendSmokeTest.csproj -c Release
```

按改动范围运行相关专项检查。构建和模拟检查不能替代真实安全模式、游戏、硬件、安装或卸载验收；在 PR 中分别写清已验证和未验证范围。

## 提交与 PR

使用简洁的动词标题，例如 `fix: 修正运行库预览指纹`、`chore: 清理构建输出约定`。PR 说明问题、最终行为、验证结果及剩余限制。逐项查看暂存区，再提交：

```powershell
git add <本次文件>
git diff --cached --stat
git diff --cached
```

不要提交玩家日志、令牌、私钥、机器配置、构建产物或安装包。大型可下载构建输入通过固定来源和哈希恢复；分发安装包使用 Releases。现有固定二进制输入保持原字节，不批量转码或重写 Git 历史。

## 清理与发布

工作区清理流程见 [维护说明](docs/WORKSPACE-MAINTENANCE.md)。默认只预览，不按“被忽略”直接判断可删除。

发布源码必须对应安装包的实际构建输入。Beta0.922 的公开源码排除了开发工作区里的 P2 卡顿检测；不要把当前开发树当作已发布源码。已发布版本的安装包、源码 ZIP、更新清单和文件对象不覆盖，同版本字节保持冻结。上传、推送标签和修改线上清单需要对应发布授权。
