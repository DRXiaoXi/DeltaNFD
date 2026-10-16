param([string]$BinDir, [string]$ObjDll, [int]$Seconds = 40)
$ErrorActionPreference = 'Continue'
$dll = Join-Path $BinDir 'DeltaNFD.dll'
Copy-Item $ObjDll $dll -Force
Write-Output ("restored: " + (Test-Path $dll))
$proc = Start-Process -FilePath (Join-Path $BinDir 'DeltaNFD.exe') -PassThru
Start-Sleep -Seconds $Seconds
Write-Output ("dll alive: " + (Test-Path $dll))
Write-Output ("process alive: " + (-not $proc.HasExited))
if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; Start-Sleep -Seconds 1 }
Write-Output ("dll alive after kill: " + (Test-Path $dll))
