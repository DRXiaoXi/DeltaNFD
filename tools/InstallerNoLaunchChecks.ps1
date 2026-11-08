# Run the actual setup preflight script against fake installation directories, never setup/uninstall.
# Invalid EXE sentinels make any attempt to launch an old frontend fail the test.
$ErrorActionPreference = 'Stop'
$scriptPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\installer\DeploymentControl.ps1'))
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('..\artifacts\setup-no-launch-' + [Guid]::NewGuid().ToString('N'))))
[void][IO.Directory]::CreateDirectory($fixtureRoot)
$passed = 0
foreach ($scenario in @('fresh', 'old-current-exe', 'legacy-name', 'both-exes', 'retry')) {
    $target = Join-Path $fixtureRoot $scenario
    [void][IO.Directory]::CreateDirectory($target)
    if ($scenario -in @('old-current-exe', 'both-exes', 'retry')) {
        [IO.File]::WriteAllText((Join-Path $target 'DeltaNFD.exe'), 'Invalid executable sentinel. Setup must never run this file.')
    }
    if ($scenario -in @('legacy-name', 'both-exes')) {
        [IO.File]::WriteAllText((Join-Path $target 'DeltaOptimizer.exe'), 'Invalid executable sentinel. Setup must never run this file.')
    }
    $evidence = Join-Path $target 'pending-evidence.json'
    [IO.File]::WriteAllText($evidence, '{"history":"must stay byte-identical"}')
    $before = (Get-FileHash -LiteralPath $evidence -Algorithm SHA256).Hash
    $runs = $(if ($scenario -eq 'retry') { 2 } else { 1 })
    for ($attempt = 1; $attempt -le $runs; $attempt++) {
        $report = Join-Path $fixtureRoot ($scenario + '-' + $attempt + '.txt')
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $scriptPath + '" -InstallRoot "' + $target + '" -Mode install -ReportPath "' + $report + '"'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $child = [Diagnostics.Process]::Start($start)
        try {
            if (-not $child.WaitForExit(30000)) { throw ('Setup preflight did not finish: ' + $scenario) }
            if ($child.ExitCode -ne 0 -or -not [IO.File]::Exists($report)) {
                throw ('Setup preflight failed: ' + $scenario + '; ' + $(if (Test-Path -LiteralPath $report) { [IO.File]::ReadAllText($report) }))
            }
            if (-not [IO.File]::ReadAllText($report).Contains('未启动旧程序')) { throw 'Missing no-launch report' }
            if ((Get-FileHash -LiteralPath $evidence -Algorithm SHA256).Hash -ne $before) { throw 'Setup changed recovery evidence' }
            $passed++
            Write-Output ('PASS: Actual setup preflight never launches frontend and preserves evidence: ' + $scenario + '/' + $attempt)
        } finally { $child.Dispose() }
    }
}
Write-Output ("Installer no-launch checks passed ($passed cases). Fixtures: $fixtureRoot")
