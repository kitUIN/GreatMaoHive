param(
    [Parameter(Mandatory)][string]$HivePath,
    [Parameter(Mandatory)][string]$BeePath,
    [string]$OutputDirectory = 'artifacts/verification/console'
)
$ErrorActionPreference = 'Stop'
$hiveExecutable = (Resolve-Path -LiteralPath $HivePath).Path
$beeExecutable = (Resolve-Path -LiteralPath $BeePath).Path
$runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$baseUrl = "http://127.0.0.1:$port"
$readKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$beeKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$headers = @{ Authorization = "Bearer $readKey" }
$entries = [Collections.Generic.List[object]]::new()

function Start-Monitor([string]$Executable, [hashtable]$Variables, [string]$Label, [string]$ConsoleEnabled) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.Environment['Updates__Enabled'] = 'false'
    $info.Environment.Remove('INVOCATION_ID') | Out-Null
    $info.Environment.Remove('Console__Enabled') | Out-Null
    if ($ConsoleEnabled) { $info.Environment['Console__Enabled'] = $ConsoleEnabled }
    foreach ($variable in $Variables.GetEnumerator()) { $info.Environment[$variable.Key] = [string]$variable.Value }
    $process = [Diagnostics.Process]::Start($info)
    $entry = [pscustomobject]@{ Process = $process; Out = $process.StandardOutput.ReadToEndAsync(); Error = $process.StandardError.ReadToEndAsync(); Label = $Label }
    $entries.Add($entry)
    return $entry
}
function Wait-Health($Entry) {
    $deadline = [datetime]::UtcNow.AddSeconds(30)
    while ([datetime]::UtcNow -lt $deadline) {
        if ($Entry.Process.HasExited) { throw "Unexpected exit: $($Entry.Label)" }
        try { if ((Invoke-RestMethod "$baseUrl/healthz" -TimeoutSec 2).status -eq 'ok') { return } } catch { }
        Start-Sleep -Milliseconds 100
    }
    throw 'Hive health endpoint did not become available.'
}
function Wait-Online([bool]$Online) {
    $deadline = [datetime]::UtcNow.AddSeconds(30)
    while ([datetime]::UtcNow -lt $deadline) {
        try {
            $node = Invoke-RestMethod "$baseUrl/api/v1/bees/console-bee" -Headers $headers -TimeoutSec 2
            if ($node.online -eq $Online -and $null -ne $node.lastSampledAt) { return }
        } catch { }
        Start-Sleep -Milliseconds 100
    }
    throw "Bee did not reach expected online state: $Online"
}
function Send-Command($Entry, [string]$Command) {
    $Entry.Process.StandardInput.WriteLine($Command)
    $Entry.Process.StandardInput.Flush()
}
function Wait-Exit($Entry) {
    if (-not $Entry.Process.WaitForExit(15000)) { throw "Console exit hung: $($Entry.Label)" }
    if ($Entry.Process.ExitCode -ne 0) { throw "Console exit failed: $($Entry.Label)" }
    return $Entry.Out.GetAwaiter().GetResult()
}
$hiveVariables = @{
    URLS = $baseUrl
    Hive__DatabasePath = (Join-Path $runDirectory 'hive.db')
    Hive__ReadApiKey = $readKey
    Hive__Bees__0__Id = 'console-bee'
    Hive__Bees__0__Token = $beeKey
    Hive__AllowInsecureTransport = 'true'
}
$beeVariables = @{
    Bee__Id = 'console-bee'
    Bee__HiveUrl = "ws://127.0.0.1:$port/agent"
    Bee__Token = $beeKey
    Bee__DatabasePath = (Join-Path $runDirectory 'bee.db')
    Bee__AllowInsecureTransport = 'true'
}
try {
    $hive = Start-Monitor $hiveExecutable $hiveVariables 'hive-commands' 'true'
    Wait-Health $hive
    $bee = Start-Monitor $beeExecutable $beeVariables 'bee-commands' 'true'
    Wait-Online $true
    Send-Command $hive 'status'
    # Use the status command's output before disconnecting Bee to avoid a pipe scheduling race.
    Start-Sleep -Milliseconds 500
    foreach ($command in @('help', 'version', 'unknown-command', 'status', 'quit')) { Send-Command $bee $command }
    $beeOutput = Wait-Exit $bee
    foreach ($expected in @('Commands:', 'GreatMaoNetBee', 'Unknown command.', 'Hive connection: Connected', 'Stopping GreatMaoNetBee')) {
        if (-not $beeOutput.Contains($expected)) { throw "Missing Bee command result: $expected" }
    }
    Wait-Online $false
    foreach ($command in @('help', 'version', 'connections', 'exit')) { Send-Command $hive $command }
    $hiveOutput = Wait-Exit $hive
    foreach ($expected in @('Commands:', 'Hive connections: 1/1 Bees online', 'Hive connections: 0/1 Bees online', 'console-bee: offline', 'Stopping GreatMaoNetHive')) {
        if (-not $hiveOutput.Contains($expected)) { throw "Missing Hive command result: $expected" }
    }
    foreach ($mode in @('true', '')) {
        $label = if ($mode) { 'hive-eof' } else { 'hive-redirected-default' }
        $entry = Start-Monitor $hiveExecutable $hiveVariables $label $mode
        Wait-Health $entry
        if (-not $mode) { Send-Command $entry 'exit' }
        $entry.Process.StandardInput.Close()
        Start-Sleep -Milliseconds 500
        Wait-Health $entry
        $entry.Process.Kill()
        $entry.Process.WaitForExit()
        if (-not $mode -and $entry.Out.GetAwaiter().GetResult().Contains('Console commands ready')) {
            throw 'Redirected stdin unexpectedly enabled interactive commands.'
        }
    }
    Write-Host 'Console smoke passed: online/offline status, commands, graceful exit, EOF and redirected-input defaults.'
} finally {
    foreach ($entry in $entries) {
        if (-not $entry.Process.HasExited) { $entry.Process.Kill(); $entry.Process.WaitForExit() }
        [IO.File]::WriteAllText((Join-Path $runDirectory "$($entry.Label).out.log"), $entry.Out.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $runDirectory "$($entry.Label).err.log"), $entry.Error.GetAwaiter().GetResult())
        $entry.Process.Dispose()
    }
}
