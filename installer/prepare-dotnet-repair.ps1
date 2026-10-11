# Fetch the pinned, unmodified Microsoft .NET runtime into the build input directory.
[CmdletBinding()]
param([switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$identity = Get-Content -LiteralPath (Join-Path $repo 'src/DeltaNFD/Services/BundledDotNetRuntime.cs') -Raw
function Read-Identity([string]$name) {
    $match = [regex]::Match($identity, ('const string ' + [regex]::Escape($name) + ' = "([^"]+)";'))
    if (!$match.Success) { throw "Missing bundled .NET identity: $name" }
    $match.Groups[1].Value
}
$fileName = Read-Identity 'FileName'
$expected = Read-Identity 'Sha512'
$downloadUrl = [uri](Read-Identity 'Url')
if ($fileName -notmatch '^dotnet-runtime-8\.0\.\d+-win-x64\.exe$' -or $expected -notmatch '^[0-9A-Fa-f]{128}$' -or
    $downloadUrl.Scheme -ne 'https' -or $downloadUrl.Host -ne 'builds.dotnet.microsoft.com' -or
    !$downloadUrl.IsDefaultPort -or $downloadUrl.UserInfo -or $downloadUrl.Fragment) { throw 'Invalid pinned runtime identity.' }
$folder = Join-Path $repo 'src/DeltaNFD/Assets/DotNetRepair'
$target = Join-Path $folder $fileName
New-Item -ItemType Directory -Path $folder -Force | Out-Null
for ($dir = Get-Item -LiteralPath $folder; $null -ne $dir; $dir = $dir.Parent) {
    if ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked .NET payload directory rejected.' }
}
if (!(Test-Path -LiteralPath $target)) {
    if ($VerifyOnly) { throw 'Bundled .NET installer is missing.' }
    $temporary = Join-Path $folder ('download-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Write-Host "Downloading pinned Microsoft .NET runtime: $downloadUrl"
        Invoke-WebRequest -Uri $downloadUrl -OutFile $temporary -UseBasicParsing -TimeoutSec 600
        $download = Get-Item -LiteralPath $temporary
        if ($download.Length -le 0 -or $download.Length -gt 157286400) { throw 'Installer size invalid.' }
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA512).Hash -ne $expected) { throw 'Official installer SHA512 mismatch.' }
        Move-Item -LiteralPath $temporary -Destination $target
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}
$installer = Get-Item -LiteralPath $target
if (($installer.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $installer.Length -le 0 -or $installer.Length -gt 157286400 -or
    (Get-FileHash -LiteralPath $target -Algorithm SHA512).Hash -ne $expected) {
    throw 'Bundled .NET installer is invalid; repair build inputs before publishing.'
}
Write-Host "Verified offline Microsoft .NET installer: $target"
