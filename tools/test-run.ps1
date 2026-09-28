param([string]$BinDir, [string]$ObjDll)

$dll = Join-Path $BinDir 'DeltaNFD.dll'

# 1. restore DLL from obj
Copy-Item $objDll $dll -Force
Write-Output ("restored: " + (Test-Path $dll))

# 2. run app
$proc = Start-Process -FilePath (Join-Path $BinDir 'DeltaNFD.exe') -PassThru
Start-Sleep -Seconds 20

# 3. check
$dllAlive = Test-Path $dll
$procAlive = -not $proc.HasExited
Write-Output ("dll alive: " + $dllAlive)
Write-Output ("process alive: " + $procAlive)

# 4. cleanup
if ($procAlive) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1 }
Write-Output ("dll alive after kill: " + (Test-Path $dll))
