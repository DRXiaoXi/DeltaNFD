#requires -Version 7.0
# Preview by default. Apply only a reviewed plan of reproducible build outputs.
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$PlanPath,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
$prefix = $repo + [IO.Path]::DirectorySeparatorChar
$gitRoot = (& git -C $repo rev-parse --show-toplevel)
if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($gitRoot).TrimEnd('\', '/') -ne $repo) {
    throw 'Run this script in the DeltaNFD Git workspace.'
}

# Preserve release deliverables, original build inputs, research and recovery evidence.
$protected = @(
    '.git', '.github', 'notes', 'server', 'licenses', 'retired', 'research', 'artifacts',
    'installer/Output', 'installer/Backup', 'src/DeltaNFD/Assets',
    '_buildcheck/Beta0.922/publish', '_buildcheck/Beta0.922/source',
    '_buildcheck/Beta0.922/files', '_buildcheck/Beta0.922/package',
    '_buildcheck/Beta0.922-RC2/publish', '_buildcheck/Beta0.922-RC2/source',
    '_buildcheck/Beta0.922-RC2/files', '_buildcheck/Beta0.922-RC2/package'
)

function Get-CheckedTarget([string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative.Contains(':') -or ($Relative -split '[/\\]') -contains '..') {
        throw "Invalid relative target: $Relative"
    }
    $relativePath = $Relative.Replace('\', '/').TrimEnd('/')
    foreach ($keep in $protected) {
        if ($relativePath -eq $keep -or $relativePath.StartsWith($keep + '/', [StringComparison]::OrdinalIgnoreCase) -or
            $keep.StartsWith($relativePath + '/', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Protected target: $relativePath"
        }
    }
    $target = [IO.Path]::GetFullPath((Join-Path $repo $relativePath))
    if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Target escaped workspace: $target"
    }
    $cursor = $target
    while ($cursor -ne $repo) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse point or linked ancestor: $cursor"
            }
        }
        $cursor = Split-Path -Parent $cursor
    }
    if (-not (Test-Path -LiteralPath $target)) { return $target }
    $leaf = Split-Path -Leaf $target
    $parts = $relativePath.Split('/')
    $standard = $leaf -in @('bin', 'obj', '__pycache__', '.vs') -and
        $parts[0] -in @('_buildcheck', 'src', 'tools', 'installer')
    $dotnetLayout = $false
    if (-not $standard -and $parts[0] -eq '_buildcheck') {
        foreach ($deps in Get-ChildItem -LiteralPath $target -Filter '*.deps.json' -File -Force) {
            $assembly = $deps.Name.Substring(0, $deps.Name.Length - 10) + '.dll'
            if (Test-Path -LiteralPath (Join-Path $target $assembly) -PathType Leaf) { $dotnetLayout = $true; break }
        }
    }
    $layout = $parts[0] -eq '_buildcheck' -and (
        $dotnetLayout -or $leaf -eq 'publish' -or $leaf -match '\.components-[a-f0-9]{32}$' -or
        (Test-Path -LiteralPath (Join-Path $target 'DeltaNFD.exe')) -or
        (Test-Path -LiteralPath (Join-Path $target 'BackendSmokeTest.exe')) -or
        (Test-Path -LiteralPath (Join-Path $target 'DotNetLayoutCheck.exe')))
    if (-not ($standard -or $layout -or $relativePath -eq 'installer/publish')) {
        throw "Target is not a recognized generated output: $relativePath"
    }
    return $target
}

if (-not $PlanPath) {
    if ($Apply) { throw '-Apply requires a reviewed -PlanPath.' }
    $rows = [Collections.Generic.List[object]]::new()
    $queue = [Collections.Generic.Stack[string]]::new()
    foreach ($name in @('_buildcheck', 'src', 'tools', 'installer')) {
        $directory = Join-Path $repo $name
        if (Test-Path -LiteralPath $directory) { $queue.Push($directory) }
    }
    while ($queue.Count -gt 0) {
        foreach ($entry in Get-ChildItem -LiteralPath $queue.Pop() -Directory -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            $relative = $entry.FullName.Substring($prefix.Length).Replace('\', '/')
            if ($entry.Name -eq '.git' -or $relative -match '^_buildcheck/workspace-(maintenance|cleanup)-') { continue }
            if (@($protected | Where-Object { $relative -eq $_ -or $relative.StartsWith($_ + '/', [StringComparison]::OrdinalIgnoreCase) }).Count) { continue }
            try { $null = Get-CheckedTarget $relative; $isOutput = $true }
            catch { $isOutput = $false }
            if ($isOutput) { $rows.Add([ordered]@{ path = $relative; reason = 'recognized generated output' }) }
            else { $queue.Push($entry.FullName) }
        }
    }
    $reportDir = Join-Path $repo ('_buildcheck/workspace-cleanup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $null = New-Item -ItemType Directory -Path $reportDir
    $PlanPath = Join-Path $reportDir 'cleanup-plan.json'
    [ordered]@{ root = $repo; targets = @($rows.ToArray()) } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $PlanPath -Encoding utf8
}

$planFullPath = [IO.Path]::GetFullPath($PlanPath)
if (-not $planFullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Keep the cleanup plan and report inside this workspace.'
}
$plan = Get-Content -LiteralPath $planFullPath -Raw -Encoding utf8 | ConvertFrom-Json
if ([IO.Path]::GetFullPath($plan.root).TrimEnd('\', '/') -ne $repo) { throw 'Plan belongs to another workspace.' }
$visible = @(& git -C $repo -c core.quotepath=false ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Cannot establish the source file boundary.' }
$targets = [Collections.Generic.List[object]]::new()
foreach ($row in $plan.targets) {
    $absolute = Get-CheckedTarget $row.path
    foreach ($source in $visible) {
        if ($source -eq $row.path -or $source.StartsWith($row.path + '/', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Target contains a Git-visible source file: $source"
        }
    }
    if (Test-Path -LiteralPath $absolute) {
        if (-not (Test-Path -LiteralPath $absolute -PathType Container)) { throw "Not a directory: $absolute" }
        $targets.Add([ordered]@{ path = $row.path; absolute = $absolute })
    }
}
Write-Host ("Plan: {0}; existing target roots: {1}" -f $planFullPath, $targets.Count)
if (-not $Apply) {
    $targets | Select-Object -First 25 | ForEach-Object { Write-Host $_.absolute }
    Write-Host 'Preview only. Inspect the JSON, preserve any needed runtime data, then pass -Apply -PlanPath.'
    return
}

# Preflight every tree before the first deletion. PowerShell does not follow directory
# links without -FollowSymlink; reject links and embedded repositories nevertheless.
foreach ($row in $targets) {
    Get-ChildItem -LiteralPath $row.absolute -Recurse -Force | ForEach-Object {
        if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $_.Name -eq '.git') {
            throw "Linked entry or embedded repository: $($_.FullName)"
        }
    }
}
$reportPath = Join-Path (Split-Path -Parent $planFullPath) 'cleanup-result.jsonl'
$done = 0
$failures = 0
foreach ($row in $targets) {
    $absolute = Get-CheckedTarget $row.path
    if (-not (Test-Path -LiteralPath $absolute)) { continue }
    if ($PSCmdlet.ShouldProcess($absolute, 'Delete reproducible build output')) {
        try {
            Remove-Item -LiteralPath $absolute -Recurse -Force
            @{ path = $row.path; status = 'removed'; utc = [DateTime]::UtcNow.ToString('o') } |
                ConvertTo-Json -Compress | Add-Content -LiteralPath $reportPath -Encoding utf8
        }
        catch {
            $failures++
            @{ path = $row.path; status = 'failed'; error = $_.Exception.Message } |
                ConvertTo-Json -Compress | Add-Content -LiteralPath $reportPath -Encoding utf8
            Write-Warning "Preserved/partial output $($row.path): $($_.Exception.Message)"
        }
    }
    $done++
    if ($done % 50 -eq 0) { Write-Host "Processed $done / $($targets.Count) roots" }
}
Write-Host "Cleanup finished. Failed roots: $failures. Report: $reportPath"
if ($failures -gt 0) { throw 'Some outputs could not be removed; inspect cleanup-result.jsonl.' }
