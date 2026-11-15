param(
    [Parameter(Mandatory=$true)][string]$ScriptPath,
    [Parameter(Mandatory=$true)][string]$OriginalPath,
    [Parameter(Mandatory=$true)][string]$ReportPath,
    [Parameter(Mandatory=$true)][int]$ExpectedMajor
)
$ErrorActionPreference = 'Stop'
$checks = [ordered]@{}
function Check([string]$Name, [bool]$Passed) {
    $checks[$Name] = $Passed
    if (!$Passed) { throw ('Check failed: '+$Name) }
}
Check 'ExpectedPowerShellVersion' ($PSVersionTable.PSVersion.Major -eq $ExpectedMajor)
$bytes = [IO.File]::ReadAllBytes($ScriptPath)
$original = [IO.File]::ReadAllBytes($OriginalPath)
Check 'Utf8BomPresent' ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)
$strictUtf8 = [Text.UTF8Encoding]::new($false,$true)
$text = $strictUtf8.GetString($bytes,3,$bytes.Length-3)
Check 'Utf8RoundTripPreservesBody' ([Convert]::ToBase64String($bytes,3,$bytes.Length-3) -ceq [Convert]::ToBase64String($strictUtf8.GetBytes($text)))
$tokens=$null; $errors=$null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath,[ref]$tokens,[ref]$errors)
Check 'ActualFileParserAcceptsFixedScript' (@($errors).Count -eq 0)
$tokens=$null; $utf8Errors=$null
[void][System.Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$utf8Errors)
Check 'ExplicitUtf8ParserAcceptsOriginalContent' (@($utf8Errors).Count -eq 0)
$ansiText = [Text.Encoding]::GetEncoding(936).GetString($original)
$tokens=$null; $ansiErrors=$null
[void][System.Management.Automation.Language.Parser]::ParseInput($ansiText,[ref]$tokens,[ref]$ansiErrors)
Check 'ReproducesOriginalAnsi936ParseFailure' (@($ansiErrors).Count -gt 0)
$tokens=$null; $originalErrors=$null
[void][System.Management.Automation.Language.Parser]::ParseFile($OriginalPath,[ref]$tokens,[ref]$originalErrors)
if ($ExpectedMajor -eq 5 -and [Text.Encoding]::Default.CodePage -eq 936) {
    Check 'ActualPowerShell51ReproducesOriginalFailure' (@($originalErrors).Count -gt 0)
}
$definitions = @($ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
    $node.Value.Contains('public static class PendulumDriverSetupV')
},$true))
Check 'OneDormantInteropDefinition' ($definitions.Count -eq 1)
# Compile declarations only. Never invoke any SCM method.
Add-Type -TypeDefinition $definitions[0].Value
$typeName = [regex]::Match($definitions[0].Value,'public static class ([A-Za-z0-9_]+)').Groups[1].Value
Check 'InteropCompilesWithoutCallingServices' ($null -ne ($typeName -as [type]))
# Status returns before the install/start/remove branches and only reads paths/registration.
$status = & $ScriptPath -Action Status
Check 'ReadOnlyStatusExecutes' ($null -ne $status -and $status.ServiceRegistered -is [bool] -and $status.DriverFileExists -is [bool] -and $status.ReceiptExists -is [bool])
$report = [ordered]@{
    PowerShell=$PSVersionTable.PSVersion.ToString()
    CodePage=[Text.Encoding]::Default.CodePage
    Checks=$checks
    OriginalActualParserErrors=@($originalErrors).Count
    OriginalAnsi936ParserErrors=@($ansiErrors).Count
    OriginalFirstErrors=@($originalErrors | Select-Object -First 3 | ForEach-Object {
        [ordered]@{ Id=$_.ErrorId; Line=$_.Extent.StartLineNumber }
    })
    FixedParserErrors=@($errors).Count
    RealServiceMutations=$false
    RealTimerRequests=$false
}
[IO.File]::WriteAllText($ReportPath,($report | ConvertTo-Json -Depth 7),[Text.UTF8Encoding]::new($false))
Write-Output ('PowerShell '+$report.PowerShell+': '+$checks.Count+' checks passed; original errors='+$report.OriginalActualParserErrors+'; fixed errors='+$report.FixedParserErrors)
