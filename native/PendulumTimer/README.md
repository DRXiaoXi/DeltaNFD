# Win10 钟摆驱动实验

此驱动独立实现 `ExSetTimerResolution` 持有机制，不包含 DPC Latency Checker 的源码或驱动。只提供 0.5～1ms 的固定长度缓冲接口；设备限 SYSTEM/管理员访问，独占句柄，停止请求、句柄 cleanup 和卸载均释放本驱动持有的请求。没有采样 DPC、任意内存访问或电源 IRP 内的计时器调用。

钟摆的 Win10 路径先尝试0.5ms，再尝试1ms；只保存驱动授予和系统回读均达标的档位。数值成功独立标为 `KernelResolutionVerified`，`VerifiedGlobal` 保持false，等待统计为空。不检测 Sleep，不运行 CPU 负载。保存值绑定新平台标识，旧扫描结果不能直接授权驱动持有；开启帧格后持有，关闭帧格或退出后端时释放。

Windows 会仲裁多个请求，当前值可能被其他更细请求覆盖。成功表示驱动持有和数值回读达标，不是独占硬锁，也不表示游戏效果已经验证。

## 构建

使用微软 MSVC x64、WDK和同构建号SDK运行 `build.ps1`，显式传入 `VcToolsRoot`、`WdkRoot`、`OutputDirectory`。构建生成未签名的 `DeltaNFDPendulumTimerV1.sys`；生产构建使用 WDK 头文件，不包含 tests 目录。

`tests/dispatch-checks.c` 用假内核 API 编译同一个驱动源文件，在用户态检查协议、请求计数与 cleanup 逻辑，不加载驱动。这不替代 Win10 上的加载、并发、电源和卸载验收。

## 实验包操作

随包驱动如果标记为测试签名，需要在允许测试签名的 Win10 x64 1809+ 测试环境中使用。它不是微软正式生产签名。安装脚本不修改证书信任、测试签名模式、安全启动或内存完整性，也不自动重启；这些前提不满足时保留错误和准备记录。

1. 关闭该环境中的帧格和旧实验包。
2. 在管理员终端中自行准备测试签名环境，导入随包公钥证书到该环境的“本地计算机 / 受信任的根证书颁发机构”和“受信任的发布者”；完成所需重启。系统策略不允许时不要继续加载，需使用符合该系统要求的正式签名驱动。
3. 查看脚本后执行：`powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\Setup-Driver.ps1 -Action Install`。
4. 启动新实验包，在钟摆页先“仅检测驱动分辨率”，再“检测并保存持有值”，然后开启帧格。核对请求值、授予值、当前分辨率；Sleep 不影响结果。
5. 关闭帧格，确认请求释放。退出工具后运行相同脚本 `-Action Remove` 停止并移除驱动。文件/服务归属或哈希变化时保留现场，脚本不会覆盖或删除未知内容。

脚本将驱动复制到受保护的 Program Files 软件目录，登记按需启动的专用驱动服务；先写准备记录再启动服务。重启后可使用同一脚本 `-Action Start` 启动已登记的驱动，不需卸载重装；此操作复核原记录、文件哈希和服务路径。加载失败时服务/记录可能保留，可按说明核对和卸载。`-Action Status` 只读查看是否登记和文件是否存在，不代表精度请求已持有。

测试环境用完后，可按随包公钥证书的具体指纹移除这两处证书，并恢复原有测试签名配置后手动重启。不要分发构建时的 PFX 或私钥文件；实验包只需要 `.sys`、公钥 `.cer`、脚本和哈希清单。

参考：[ExSetTimerResolution](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-exsettimerresolution)、[内核签名要求](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-requirements--windows-vista-and-later-)。
