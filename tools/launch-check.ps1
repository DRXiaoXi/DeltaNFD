$ErrorActionPreference = 'Continue'
Set-Location 'D:\我要干大活\三角帧不掉洲'
$bin = 'src\DeltaNFD\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64'
$proc = Start-Process -FilePath (Join-Path $bin 'DeltaNFD.exe') -PassThru
Start-Sleep -Seconds 12
Write-Output ("dll alive: " + (Test-Path (Join-Path $bin 'DeltaNFD.dll')))
if ($null -ne $proc) {
    Write-Output ("process alive: " + (-not $proc.HasExited))
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; Start-Sleep -Seconds 1 }
} else {
    Write-Output "process: launch failed"
}
