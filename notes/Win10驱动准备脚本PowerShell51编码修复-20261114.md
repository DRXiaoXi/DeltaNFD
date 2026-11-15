# Win10 驱动准备脚本 PowerShell 5.1 编码修复

用户在Win10虚拟机查询到DeltaNFDPendulumTimerV1服务不存在（1060），随后执行准备脚本Start时出现中文乱码和第17/24/25行语法错误。服务尚未安装，需要使用Install；但编码问题会在任何动作执行之前阻止整个脚本解析。

根因是原脚本使用无BOM的UTF-8，Windows PowerShell 5.1按ACP936解码，中文字符串被误读，后续C# here-string也被当作PowerShell语法。前轮PowerShell 7的解析检查没有覆盖5.1的文件解码行为。

本轮仅在`native/PendulumTimer/Setup-Driver.ps1`原始字节前增加EF BB BF，正文逐字节不变。新增`native/PendulumTimer/tests/Test-SetupEncoding.ps1`，明确断言实际PowerShell主版本，覆盖BOM、正文不变、文件解析、显式UTF-8/错误ACP936对照、C#声明编译和只读Status执行。只编译声明，不调用其中任何服务API。

实际Windows PowerShell 5.1.26100.9444、ACP936下原文件81个解析错误，前3行17/24/25与用户反馈一致；修后0，10项通过。PowerShell 7.6.9下9项通过。初次通过exec的shell选项未切换到5.1，记录中的版本明确为7.6.9，未将其计为5.1验收；随后使用Python显式启动系统powershell.exe并断言版本，原记录保留。

交付小补丁ZIP，仅含修正的`Drivers/PendulumTimer/Setup-Driver.ps1`及说明；原完整r2 ZIP、旧包内脚本、测试签名SYS及原生C源码保留。补丁解包后另行执行同一5.1检查；检查结果、归档及保护SHA见`_buildcheck/win10-driver-ps51-encoding-20261114/final-result.json`。

仍Beta0.932-RC2/0.9321；未重编主程序或驱动、未同步根产品源码、未提交上传发布或改通道。未在宿主或虚拟机执行Install/Start/Remove、改信任或启动设置、请求计时器、关程序或重启。虚拟机实际安装、加载和分辨率结果仍待用户操作确认。
