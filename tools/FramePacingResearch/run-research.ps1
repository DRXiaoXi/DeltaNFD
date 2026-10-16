param(
    [int]$TargetFps = 144,
    [int]$Frames = 8640,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot ('../../_buildcheck/frame-pacing/run-' + [Guid]::NewGuid().ToString('N'))),
    [switch]$Dx12Smoke
)
$ErrorActionPreference = 'Stop'
if ($TargetFps -lt 60 -or $TargetFps -gt 1000 -or $TargetFps % 2) { throw 'Research batch requires even FPS 60..1000 (for synthetic 2x).' }
if ($Frames -lt 100 -or $Frames -gt 200000) { throw 'Frames must be 100..200000.' }
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Research output must be a fresh directory.' }
New-Item -ItemType Directory -Path $root | Out-Null
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
& (Join-Path $repo 'tools/FramePacingLab/build.ps1') *> (Join-Path $root 'native-build.log')
$lab = Join-Path $repo '_buildcheck/frame-pacing/native/FramePacingLab.exe'
& dotnet build (Join-Path $PSScriptRoot 'FramePacingResearch.csproj') -p:NuGetAudit=false -m:1 -nr:false -v:q *> (Join-Path $root 'analysis-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Analysis build failed.' }
$analysis = Join-Path $PSScriptRoot 'bin/Debug/net8.0-windows/FramePacingResearch.dll'
& $lab --self-test *> (Join-Path $root 'core-checks.log')
if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
& $lab --clock-test *> (Join-Path $root 'clock-checks.log')
if ($LASTEXITCODE -ne 0) { throw 'Windows timer checks failed.' }
& dotnet $analysis self-test *> (Join-Path $root 'analysis-checks.log')
if ($LASTEXITCODE -ne 0) { throw 'Analysis checks failed.' }
$rows = [Collections.Generic.List[object]]::new()
foreach ($mode in @('absolute','relative','none')) {
    foreach ($scenario in @('steady','jitter','long-frame','generated2x')) {
        $name = $mode + '-' + $scenario
        $csv = Join-Path $root ($name + '.csv')
        & $lab --simulate --fps $TargetFps --frames $Frames --mode $mode --scenario $scenario --output $csv
        if ($LASTEXITCODE -ne 0) { throw ('Simulation failed: ' + $name) }
        $destination = Join-Path $root ($name + '-analysis')
        & dotnet $analysis analyze --csv $csv --target $TargetFps --output $destination
        if ($LASTEXITCODE -ne 0) { throw ('Analysis failed: ' + $name) }
        $report = Get-Content -LiteralPath (Join-Path $destination 'analysis.json') -Raw | ConvertFrom-Json
        $stats = $report.cpuSubmissionIntervals
        if ($scenario -eq 'generated2x') { $stats = $report.displayIntervals }
        $rows.Add([pscustomobject]@{Source='synthetic'; Mode=$mode; Scenario=$scenario; MeanFps=(1000/$stats.meanMs); P95Ms=$stats.p95Ms; P99Ms=$stats.p99Ms; StdDevMs=$stats.standardDeviationMs; LongFrames=$stats.longFrames})
    }
}
$timeoutCsv = Join-Path $root 'absolute-timer-timeout.csv'
& $lab --simulate --fps $TargetFps --frames 100 --scenario timer-timeout --output $timeoutCsv
if ($LASTEXITCODE -ne 2) { throw 'Timeout fixture did not fail explicitly.' }
& dotnet $analysis analyze --csv $timeoutCsv --target $TargetFps --output (Join-Path $root 'absolute-timer-timeout-analysis')
if ($LASTEXITCODE -ne 0) { throw 'Timeout evidence analysis failed.' }
if ($Dx12Smoke) {
    & $lab --warp --hidden --seconds 3 --fps $TargetFps --output (Join-Path $root 'own-dx12-warp.csv')
    if ($LASTEXITCODE -ne 0) { throw 'Own DX12 WARP smoke failed; see run output.' }
    & dotnet $analysis analyze --csv (Join-Path $root 'own-dx12-warp.csv') --target $TargetFps --output (Join-Path $root 'own-dx12-analysis')
    if ($LASTEXITCODE -ne 0) { throw 'Own DX12 analysis failed.' }
}
$rows | Export-Csv -LiteralPath (Join-Path $root 'summary.csv') -NoTypeInformation -Encoding utf8
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Deterministic frame pacing research')
$lines.Add('')
$lines.Add('Synthetic clock/workload only. Relative is a deliberately simple post-work relative-wait baseline, NOT NVIDIA or game-native limiter data. None is synthetic uncapped work, NOT a real uncapped game run.')
$lines.Add('')
$lines.Add('| Mode | Scenario | Mean cadence FPS | P95 ms | P99 ms | Std dev ms | Long frames |')
$lines.Add('|---|---|---:|---:|---:|---:|---:|')
foreach ($row in $rows) {
    $lines.Add([string]::Format([Globalization.CultureInfo]::InvariantCulture, '| {0} | {1} | {2:F3} | {3:F4} | {4:F4} | {5:F4} | {6} |', $row.Mode,$row.Scenario,$row.MeanFps,$row.P95Ms,$row.P99Ms,$row.StdDevMs,$row.LongFrames))
}
$lines.Add('')
$lines.Add('Synthetic generated2x uses offline ideal midpoint interpolation between two known native frames (future information), NOT a real-time generator. It does not prove DLSS/MFG pacing, native/generated detection, or Delta Force integration. Long render work remains visible; this scheduler does not create missing performance.')
$lines.Add('')
$lines.Add('DX12 smoke, if requested, uses hidden WARP software rendering and records CPU submission timestamps; no physical display or hardware-game performance claim.')
[IO.File]::WriteAllLines((Join-Path $root 'summary.md'), $lines, [Text.UTF8Encoding]::new($false))
$meta = [ordered]@{Source='synthetic research'; TargetFps=$TargetFps; Frames=$Frames; Seed=20261010; CreatedUtc=[DateTime]::UtcNow.ToString('O'); Os=[Environment]::OSVersion.Version.ToString(); LabSha256=(Get-FileHash -LiteralPath $lab -Algorithm SHA256).Hash; GameMeasurementsPerformed=$false; InjectsIntoGame=$false; WritesGameOrDriverSettings=$false}
[IO.File]::WriteAllText((Join-Path $root 'research.json'), ($meta | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Output ('RESEARCH_ROOT=' + $root)
