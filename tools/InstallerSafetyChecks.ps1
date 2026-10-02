# Structural regression checks only. Never runs setup, uninstall, or process termination.
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '..\installer\DeltaNFD.iss'
$source = [IO.File]::ReadAllText($scriptPath)

function Assert-Safety([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Output "PASS: $Message"
}

$uninstall = $source.Substring($source.IndexOf('procedure CurUninstallStepChanged('))
$pre = $uninstall.Substring(0, $uninstall.IndexOf('if CurUninstallStep <> usPostUninstall'))
Assert-Safety ($pre.Contains('if not KillAppProcesses()') -and $pre.Contains('Abort;')) 'Process verification must precede file uninstall and abort on failure'
Assert-Safety ($pre.Contains('if not RestoreRuntimeGuardBeforeUninstall()')) 'Runtime protection is restored before uninstall deletes files or backups'
Assert-Safety ($source.Contains("'--restore-runtime-guard-for-uninstall'")) 'Uninstaller invokes the shared headless restore path'
Assert-Safety ($source.Contains('ewWaitUntilTerminated, ResultCode) and (ResultCode = 0)')) 'Uninstaller waits for successful restore exit code'
$appSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\src\DeltaNFD\App.xaml.cs'))
Assert-Safety ($appSource.IndexOf('"--restore-runtime-guard-for-uninstall"') -lt $appSource.IndexOf('SingleInstanceGuard.TryBecomePrimary()')) 'Headless restore runs before GUI single-instance handling'
$headless = [regex]::Match($appSource, 'private static async Task RestoreRuntimeGuardForUninstallAsync\(\)[\s\S]+?Environment.Exit\(code\);').Value
Assert-Safety ($headless.Contains('DisableAsync()') -and -not $headless.Contains('ServiceLocator.Frame')) 'Headless restore does not start frame optimizations'
Assert-Safety ($headless.Contains('PrepareForRemovalAsync(restoreCpuSets: true)')) 'Headless restore drains offline helper and restores CPU Sets before uninstall'
Assert-Safety (-not $uninstall.Substring($pre.Length).Contains('KillAppProcesses();')) 'No late process termination after file uninstall'
Assert-Safety ($source.Contains('function PreserveBackupFiles') -and $source.Contains('CopyFile(Src, Dst, True)')) 'Backup copying returns status and cannot overwrite existing files'
Assert-Safety ($source.Contains('GetSHA256OfFile(Src) <> GetSHA256OfFile(Dst)')) 'Backup copies are verified before deletion'
$backupFailure = [regex]::Match($uninstall, 'if not PreserveBackupFiles[\s\S]+?end;').Value
Assert-Safety ($backupFailure.Contains('LegacyDataDir') -and $backupFailure.Contains('Exit;')) 'Copy failure preserves current and legacy data'
Assert-Safety ($source.Contains('function DelTree(const Path, Root: String)') -and $source.Contains('if HasReparseAncestor(Path) then')) 'Recursive cleanup has root and ancestor guards'
Assert-Safety ($source.Contains('FindRec.Attributes and ReparsePointAttribute')) 'Recursive cleanup does not follow reparse entries'

# Reconstruct the actual Pascal command literals, then parse (never execute) the PowerShell.
$assignment = [regex]::Match($source, "Command := ([\s\S]+?);\r?\n").Groups[1].Value
Assert-Safety ($assignment.Length -gt 0) 'Process command can be extracted'
foreach ($root in @('C:\Program Files\DeltaOptimizer', "D:\Games\A'&B")) {
    $command = ''
    $tokens = [regex]::Matches($assignment, "'(?:''|[^'])*'|\bRoot\b")
    foreach ($token in $tokens) {
        if ($token.Value -eq 'Root') { $command += $root.Replace("'", "''") }
        else { $command += $token.Value.Substring(1, $token.Value.Length - 2).Replace("''", "'") }
    }
    $parseTokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseInput($command, [ref]$parseTokens, [ref]$parseErrors)
    Assert-Safety ($parseErrors.Count -eq 0) "Process command parses with directory: $root"
    Assert-Safety ($command.Contains('[IO.Path]::GetDirectoryName($p.ExecutablePath) -ieq $root')) 'Process termination is scoped to installation directory'
    Assert-Safety ($command.Contains('if ($left.Count) { exit 1 }')) 'Remaining processes produce a failure exit code'
}
Write-Output 'Installer safety structural checks passed; runtime uninstall validation still requires an isolated VM.'
