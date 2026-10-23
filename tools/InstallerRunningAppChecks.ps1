# Real preflight + owned fake app; never run setup or terminate an application.
param([string]$SmokeDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $PSScriptRoot 'BackendSmokeTest/bin/Release/net8.0-windows'
if ($SmokeDirectory) { $bin = [IO.Path]::GetFullPath($SmokeDirectory) }
$control = Join-Path $repo 'installer/DeploymentControl.ps1'
$fixture = Join-Path $repo ('artifacts/运行中安装 回归测试-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$windowsPowerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
foreach ($scenario in @(@{ Code = 0; Delay = 0 }, @{ Code = 10; Delay = 0 }, @{ Code = 2; Delay = 0 }, @{ Code = 0; Delay = 3500 })) {
    $code = $scenario.Code
    $root = Join-Path $fixture ('case-' + $code + '-' + $scenario.Delay)
    [void][IO.Directory]::CreateDirectory($root)
    Get-ChildItem -LiteralPath $bin -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $root }
    Copy-Item -LiteralPath (Join-Path $bin 'BackendSmokeTest.exe') -Destination (Join-Path $root 'DeltaNFD.exe')
    $nonce = [Guid]::NewGuid().ToString('D')
    [IO.File]::WriteAllText((Join-Path $root 'isolated-test.marker'), $nonce)
    $report = Join-Path $root 'report.txt'
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $root 'DeltaNFD.exe'
    $start.Arguments = '--deployment-install-fixture "' + $root + '" ' + $code + ' ' + $nonce + ' ' + $scenario.Delay
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $app = [Diagnostics.Process]::Start($start)
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not (Test-Path -LiteralPath (Join-Path $root 'fixture-ready.txt'))) {
            if ($app.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Owned app fixture did not start' }
            Start-Sleep -Milliseconds 50
        }
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $windowsPowerShell
        $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $control + '" -InstallRoot "' + $root + '" -Mode install -ReportPath "' + $report + '"'
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $child = [Diagnostics.Process]::Start($start)
        try {
            if (-not $child.WaitForExit(30000)) { throw 'Preflight did not finish' }
            $detail = [IO.File]::ReadAllText($report)
            $reportBytes = [IO.File]::ReadAllBytes($report)
            if ($reportBytes.Length -lt 3 -or $reportBytes[0] -ne 239 -or $reportBytes[1] -ne 187 -or $reportBytes[2] -ne 191) { throw 'Report must be UTF-8 BOM for Inno Setup' }
            $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
            $decoded = $strictUtf8.GetString($reportBytes, 3, $reportBytes.Length - 3)
            if ($decoded -ne $detail -or -not $detail.Contains('隔离测试')) { throw 'Chinese report encoding did not round-trip' }
            if ($child.ExitCode -ne $code) { throw ('Preflight exit=' + $child.ExitCode + ', expected=' + $code + '; ' + $detail) }
            if ([IO.File]::ReadAllText((Join-Path $root 'fixture-request.txt')) -ne 'INSTALL/1') { throw 'Missing authenticated install request' }
            if ($code -eq 2 -and $app.HasExited) { throw 'Blocked app must stay alive' }
            if ($code -ne 2 -and -not $app.WaitForExit(5000)) { throw 'Ready app did not exit naturally' }
            Write-Output ('PASS: Real preflight with running owned app, code=' + $code + ', listener delay=' + $scenario.Delay + '; UTF-8 Chinese report verified; ' + $detail)
        } finally { $child.Dispose() }
    } finally {
        [IO.File]::WriteAllText((Join-Path $root 'fixture-finish.txt'), 'finish')
        if (-not $app.WaitForExit(45000)) { throw 'Owned fixture has not finished; never force-killed' }
        $app.Dispose()
    }
}
Write-Output ('Running-app install checks passed. Fixtures: ' + $fixture)
