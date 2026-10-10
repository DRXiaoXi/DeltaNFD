# Download/extract Microsoft's redistributable only. Never executes DXSETUP or repairs the host.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $repo 'src\DeltaNFD\Assets\DirectXRepair'
$template = Join-Path $PSScriptRoot 'directx-payload-manifest.json'
$manifest = [IO.File]::ReadAllText($template) | ConvertFrom-Json
function Test-Payload {
    foreach ($file in $manifest.Files) {
        $path = Join-Path $destination $file.Path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
        if ((Get-Item -LiteralPath $path).Length -ne $file.Length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Sha256) { return $false }
    }
    return $true
}
if (-not (Test-Payload)) {
    if (Test-Path -LiteralPath $destination) { throw 'DirectX resource directory exists but fails verification. Preserve it for review before rebuilding.' }
    $inputRoot = Join-Path $repo ('_buildcheck\directx-input-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($inputRoot)
    $redist = Join-Path $inputRoot 'directx_Jun2010_redist.exe'
    Invoke-WebRequest -Uri 'https://download.microsoft.com/download/8/4/a/84a35bf1-dafe-4ae8-82af-ad2ae20b6b14/directx_Jun2010_redist.exe' -OutFile $redist
    if ((Get-FileHash -LiteralPath $redist -Algorithm SHA256).Hash -ne $manifest.RedistSha256) { throw 'Microsoft redistributable SHA256 mismatch' }
    $sevenZip = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
    if (-not (Test-Path -LiteralPath $sevenZip)) { throw '7-Zip is required to extract the signed redistributable without executing it.' }
    & $sevenZip x $redist ('-o' + $destination) -y | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Payload)) { throw 'DirectX resources failed extraction or hash verification' }
}
Copy-Item -LiteralPath $template -Destination (Join-Path $destination 'payload-manifest.json')
Write-Output 'Microsoft DirectX resources verified; no installer was executed.'
