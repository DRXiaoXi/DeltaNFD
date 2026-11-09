# Alpha0.931-RC5 / 0.9318 构建输入

本地提交对应内测产品源码；本轮不上传GitHub。使用 Windows x64、.NET SDK 8.0.425、Python 3.11+；安装器另需 Inno Setup 6。

先运行 `python tools/prepare-source-assets.py` 校验并恢复固定厂商依赖，再运行 `installer/publish-selfcontained.ps1` 生成自包含布局，最后使用 `installer/DeltaNFD.iss` 生成安装器。引导只下载并核验资源，不运行厂商安装器。固定清单包括 0.9200 原始资源、0.9299 官方14.44资源及0.9315 DirectX资源，详见引导脚本及其固定哈希。

`SOURCE_FILES_SHA256.json` 按 Git 对象内容记录公开源文件 SHA256（不包含自身），Git checkout 的换行转换不改变源对象身份。未入库的二进制依赖按固定资源清单恢复；完整构建输入和验收记录另在本地保留。不承诺安装器逐字节可重现。

产品为六维护后端、自包含.NET8布局。排除运行库研究策略、已退役卡顿检测、反馈内容、凭据和本地日志。根 update.json 为历史样例，不是实际线上清单。验证以隔离夹具、模拟写入及只读检查为主，不代表实际游戏、真实维护或安装升级已验收。
