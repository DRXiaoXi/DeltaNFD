[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceLayout,
    [Parameter(Mandatory = $true)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceLayout).Path
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) { throw 'Destination must not exist; existing layouts are never overwritten.' }
if ($destinationPath.StartsWith($source.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination must be outside source layout.' }
$components = @('Backends/ace','Backends/gpu','Backends/runtime','Backends/system','Backends/cpuframe','Backends/procman','Backends/updater')
$files = @{}
foreach ($component in $components) {
    $directory = Join-Path $source $component
    if (!(Test-Path -LiteralPath $directory -PathType Container)) { throw "Missing component: $component" }
    if ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked component rejected.' }
    foreach ($file in Get-ChildItem -LiteralPath $directory -Recurse -Force) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked entry rejected: $($file.FullName)" }
        if ($file.PSIsContainer) { continue }
        $relative = $file.FullName.Substring($directory.TrimEnd('\').Length + 1)
        $stream = [IO.File]::OpenRead($file.FullName)
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
        finally { $algorithm.Dispose(); $stream.Dispose() }
        if ($files.ContainsKey($relative)) {
            if ($files[$relative].Hash -ne $hash) { throw "Shared dependency conflict: $relative ($component). Rebuild all components consistently." }
        } else { $files[$relative] = @{ Path = $file.FullName; Hash = $hash } }
    }
}
# Validate all collisions before writing output; differing binaries are never replaced.
New-Item -ItemType Directory -Path $destinationPath | Out-Null
foreach ($relative in $files.Keys) {
    $target = Join-Path $destinationPath $relative
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    Copy-Item -LiteralPath $files[$relative].Path -Destination $target
}
Write-Host "Shared backend layout: $destinationPath ($($files.Count) unique files)"
