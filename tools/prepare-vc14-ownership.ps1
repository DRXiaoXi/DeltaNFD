# Downloads/copies a fixed Microsoft original for read-only ownership evidence. Never runs it.
[CmdletBinding()]
param([string]$Source = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$catalog = @(Get-Content -LiteralPath (Join-Path $repo 'src/DeltaNFD.Execution/Execution/Vc14OwnershipOriginals.catalog.json') -Raw | ConvertFrom-Json)
$root = Join-Path $repo 'src/DeltaNFD/Assets/Vc14OwnershipOriginals'
New-Item -ItemType Directory -Path $root -Force | Out-Null
for ($part = Get-Item -LiteralPath $root; $null -ne $part; $part = $part.Parent) {
    if ($part.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Ownership source root contains a reparse point' }
}
foreach ($entry in $catalog) {
    if ([IO.Path]::GetFileName($entry.File) -ne $entry.File -or ([uri]$entry.Url).Scheme -ne 'https' -or ([uri]$entry.Url).Host -ne 'download.visualstudio.microsoft.com') { throw 'Unexpected catalog path or host' }
    $destination = Join-Path $root $entry.File
    $temporary = Join-Path $root ('.ownership-' + [guid]::NewGuid().ToString('N') + '.exe')
    try {
        if ($Source) { Copy-Item -LiteralPath $Source -Destination $temporary }
        elseif (Test-Path -LiteralPath $destination -PathType Leaf) { Copy-Item -LiteralPath $destination -Destination $temporary }
        else { Invoke-WebRequest -Uri $entry.Url -OutFile $temporary -TimeoutSec 60 }
        $file = Get-Item -LiteralPath $temporary
        if ($file.Length -ne $entry.Bytes -or (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $entry.Sha256) { throw 'Ownership original hash/size mismatch' }
        $signature = Get-AuthenticodeSignature -LiteralPath $temporary
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation,') { throw 'Ownership original Microsoft signature invalid' }
        if ($file.VersionInfo.FileVersion -ne $entry.Version -or $file.VersionInfo.FileDescription -notmatch ('Redistributable \(' + $entry.Architecture + '\)')) { throw 'Ownership original version/architecture mismatch' }
        # The fixed hash pins the entire original. Production additionally verifies its signed .wixburn ID.
        Move-Item -LiteralPath $temporary -Destination $destination -Force
        Write-Host ('Prepared read-only ownership original: ' + $entry.File + '; no installer executed.')
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
