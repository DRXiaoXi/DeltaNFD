# Exercise actual native lookup, pipe constructors, PID authentication and response parsing.
# The only server is an in-memory fixture in this test PowerShell process.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\installer\DeploymentControl.ps1') -LibraryOnly
Initialize-ProcessQuery
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
public static class DeploymentPipeFixture {
    public static Task<string> Start(string name, string response) {
        // Construct synchronously so the endpoint exists before the client connects.
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        return Task.Run(async () => {
            using(pipe) {
                await pipe.WaitForConnectionAsync();
                using(var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                using(var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true)) {
                    var request = await reader.ReadLineAsync();
                    writer.AutoFlush = true;
                    await writer.WriteLineAsync(response);
                    return request;
                }
            }
        });
    }
}
'@
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('..\artifacts\pipe-fixture-' + [Guid]::NewGuid().ToString('N'))))
$self = [Diagnostics.Process]::GetCurrentProcess()
try {
    $image = [DeltaDeployment.Native]::Image($self.Id)
    if (-not [IO.Path]::IsPathRooted($image)) { throw 'Native image query did not return an absolute path' }
    Write-Output 'PASS: Native image query reads owned process without WMI'
    $owner = [pscustomobject]@{ Process = $self; StartTicks = $self.StartTime.ToUniversalTime().Ticks; Image = $image }
    $detail = 'fixture history retained'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($detail))
    $server = [DeploymentPipeFixture]::Start((Get-DeploymentPipeName $root), ('10|' + $encoded))
    $reply = Request-CooperativeExit $root 'uninstall' $owner
    if ($reply.Code -ne 10 -or $reply.Detail -ne $detail) { throw ('Production response parsing failed: ' + ($reply | ConvertTo-Json -Compress)) }
    if (-not $server.Wait(5000) -or $server.Result -ne 'UNINSTALL/1') { throw 'Production request encoding failed' }
    Write-Output 'PASS: Production pipe authenticates server PID/start time and preserves history result'
    $server = [DeploymentPipeFixture]::Start((Get-DeploymentPipeName $root), 'bad-response')
    $rejected = $false
    try { Request-CooperativeExit $root 'install' $owner | Out-Null } catch { $rejected = $_.Exception.Message.Contains('有效') }
    if (-not $rejected) { throw 'Malformed result was accepted' }
    if (-not $server.Wait(5000)) { throw 'Malformed fixture did not finish' }
    Write-Output 'PASS: Malformed deployment response cannot grant permission'
    $name = 'DeltaNFD_7A3E9C4D_DeploymentFixture_' + [Guid]::NewGuid().ToString('N')
    $mutex = New-Object Threading.Mutex($false, ('Local\' + $name))
    try {
        $opened = [Threading.Mutex]::OpenExisting('Local\' + $name)
        $opened.Dispose()
        Write-Output 'PASS: Installer mutex can be queried without taking ownership'
    } finally { $mutex.Dispose() }
} finally { $self.Dispose() }
Write-Output 'Deployment native pipe checks passed (4 assertions). No application processes were terminated.'
