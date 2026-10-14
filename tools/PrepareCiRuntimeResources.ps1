# Prepare pinned redistributable test inputs, never execute their installers.
[CmdletBinding()]
param([string]$ArchivePath = '', [string]$DestinationRoot = '')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
if (!$DestinationRoot) { $DestinationRoot = $repo }
$destination = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\')
if ($destination -ne $repo -and !$destination.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination must stay within the workspace.' }
$expected = 'fd8ace56405aebaee3843688a33bc4bcffd620e5b03eda9a182fed125b24d0e4'
if (!$ArchivePath) {
    $ArchivePath = Join-Path $repo '_buildcheck/ci-runtime-inputs/source.zip'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $ArchivePath)) | Out-Null
    if (!(Test-Path -LiteralPath $ArchivePath)) {
        Invoke-WebRequest -Uri 'https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.914/DeltaNFD_Source_0.914.zip' -OutFile $ArchivePath
    }
}
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Pinned source input archive SHA256 mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
$count = 0
try {
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\','/') -replace '^DeltaNFD_Source_0\.914/', ''
        if (!$entry.Name -or ($name -notmatch '^src/DeltaNFD/Assets/(?:VCRedistRepair/|DirectXRepair/)' -and
            $name -notmatch '^src/DeltaNFD/Assets/VCRedist2015-2022-(?:x64|x86)\.exe$')) { continue }
        if ($name -match '(?:^|/)\.\.(?:/|$)' -or $entry.Length -gt 157286400 -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Unsafe input archive entry.' }
        $target = [IO.Path]::GetFullPath((Join-Path $destination $name))
        $assetRoot = Join-Path $destination 'src/DeltaNFD/Assets'
        if (!$target.StartsWith($assetRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Input path escaped Assets.' }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        for ($parent = Get-Item -LiteralPath (Split-Path -Parent $target); $parent; $parent = $parent.Parent) {
            if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked input directory rejected.' }
        }
        if (Test-Path -LiteralPath $target) {
            $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') } finally { $stream.Dispose(); $sha.Dispose() }
            if ((Get-FileHash -LiteralPath $target).Hash -ne $hash) { throw ('Existing input differs; not overwriting: ' + $name) }
        } else { [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false) }
        $count++
    }
} finally { $archive.Dispose() }
foreach ($name in @('VCRedistRepair/Installer.cmd','VCRedistRepair/payload_manifest_sha256.csv','DirectXRepair/DXSETUP.exe','DirectXRepair/payload-manifest.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $destination ('src/DeltaNFD/Assets/' + $name)))) { throw ('Missing required test input: ' + $name) }
}
Write-Output ('Pinned runtime resources prepared: ' + $count + ' files. No installer or archived source code executed.')
