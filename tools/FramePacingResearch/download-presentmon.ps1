param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../../_buildcheck/frame-pacing/dependencies'))
$ErrorActionPreference = 'Stop'
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PresentMon.lock.json') -Raw | ConvertFrom-Json
$directory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$destination = Join-Path $directory $pin.fileName
if (Test-Path -LiteralPath $destination) {
    if ((Get-Item -LiteralPath $destination).Length -ne $pin.size -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $pin.sha256) {
        throw 'Existing dependency is not the pinned executable; refusing to overwrite it.'
    }
} else {
    $temporary = Join-Path $directory ([Guid]::NewGuid().ToString('N') + '.download')
    try {
        Invoke-WebRequest -Uri $pin.url -OutFile $temporary -TimeoutSec 60
        if ((Get-Item -LiteralPath $temporary).Length -ne $pin.size -or (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $pin.sha256) { throw 'PresentMon size/hash verification failed.' }
        Move-Item -LiteralPath $temporary -Destination $destination
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
Write-Output $destination
Write-Output 'Pinned console collector only. No installer, service registration or game configuration changes.'
