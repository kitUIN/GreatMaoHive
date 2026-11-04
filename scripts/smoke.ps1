param(
    [Parameter(Mandatory)][string]$HivePath,
    [Parameter(Mandatory)][string]$BeePath,
    [string]$OutputDirectory = 'artifacts/verification/smoke',
    [ValidateRange(0,300)][int]$WarmupSeconds = 15,
    [ValidateRange(1,300)][int]$ObservationSeconds = 30,
    [string]$TransferUrl,
    [switch]$SkipRestart
)
$ErrorActionPreference = 'Stop'
$hiveExecutable = (Resolve-Path -LiteralPath $HivePath).Path
$beeExecutable = (Resolve-Path -LiteralPath $BeePath).Path
$resultDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($resultDirectory) | Out-Null
$runDirectory = Join-Path $resultDirectory ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$baseUrl = "http://127.0.0.1:$port"
$readKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$beeKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$headers = @{ Authorization = "Bearer $readKey" }
$startedProcesses = [Collections.Generic.List[object]]::new()

function Start-Monitor([string]$Executable, [hashtable]$Variables, [string]$Label) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($item in $Variables.GetEnumerator()) { $info.Environment[$item.Key] = [string]$item.Value }
    $process = [Diagnostics.Process]::Start($info)
    $entry = [pscustomobject]@{ Process = $process; Out = $process.StandardOutput.ReadToEndAsync(); Error = $process.StandardError.ReadToEndAsync(); Label = $Label }
    $startedProcesses.Add($entry)
    return $process
}
function Wait-Node([datetimeoffset]$After) {
    $deadline = [datetime]::UtcNow.AddSeconds(90)
    while ([datetime]::UtcNow -lt $deadline) {
        try {
            $node = Invoke-RestMethod "$baseUrl/api/v1/bees/smoke-bee" -Headers $headers -TimeoutSec 3
            if ($node.online -and $null -ne $node.totals -and [datetimeoffset]$node.lastSampledAt -gt $After) { return $node }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    throw 'Bee did not become online with a fresh persisted sample.'
}
$hiveVariables = @{
    URLS = $baseUrl
    Hive__DatabasePath = (Join-Path $runDirectory 'hive.db')
    Hive__ReadApiKey = $readKey
    Hive__Bees__0__Id = 'smoke-bee'
    Hive__Bees__0__Token = $beeKey
    Hive__AllowInsecureTransport = 'true'
}
$beeVariables = @{
    Bee__Id = 'smoke-bee'
    Bee__Name = 'Smoke Bee'
    Bee__HiveUrl = "ws://127.0.0.1:$port/agent"
    Bee__Token = $beeKey
    Bee__DatabasePath = (Join-Path $runDirectory 'bee.db')
    Bee__AllowInsecureTransport = 'true'
}
try {
    $started = [datetime]::UtcNow
    $hiveProcess = Start-Monitor $hiveExecutable $hiveVariables 'hive-1'
    $beeProcess = Start-Monitor $beeExecutable $beeVariables 'bee-1'
    $first = Wait-Node $started
    $current = Invoke-RestMethod "$baseUrl/api/v1/bees/smoke-bee/traffic/current" -Headers $headers -TimeoutSec 5
    if (-not $current.available) { throw 'No usable network sample on this host.' }
    Start-Sleep -Seconds $WarmupSeconds
    $beeProcess.Refresh()
    $cpuStart = $beeProcess.TotalProcessorTime.TotalSeconds
    $observedStart = [datetime]::UtcNow
    Start-Sleep -Seconds $ObservationSeconds
    $beeProcess.Refresh()
    $cpuCorePercent = 100 * ($beeProcess.TotalProcessorTime.TotalSeconds - $cpuStart) / ([datetime]::UtcNow - $observedStart).TotalSeconds
    $workingSetMiB = $beeProcess.WorkingSet64 / 1MB
    $latest = Wait-Node $started
    if ($latest.totals.totalBytes -lt $first.totals.totalBytes) { throw 'Totals regressed.' }
    $transfer = $null
    if ($TransferUrl) {
        $before = Wait-Node ([datetimeoffset]::UtcNow)
        $ids = $before.node.interfaces.id
        $networkInterfaces = [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object { $_.Id -in $ids }
        $beforeCounter = ($networkInterfaces | ForEach-Object { $_.GetIPStatistics().BytesReceived } | Measure-Object -Sum).Sum
        $download = Invoke-WebRequest -Uri $TransferUrl -TimeoutSec 30
        $payloadBytes = $download.RawContentLength
        $afterCounter = ($networkInterfaces | ForEach-Object { $_.GetIPStatistics().BytesReceived } | Measure-Object -Sum).Sum
        $after = Wait-Node ([datetimeoffset]::UtcNow)
        $monitoredDelta = $after.totals.receivedBytes - $before.totals.receivedBytes
        if ($payloadBytes -le 0 -or $monitoredDelta -lt $payloadBytes -or ($afterCounter - $beforeCounter) -lt $payloadBytes) {
            throw 'Downloaded payload was not reflected in the selected network interface and persisted total.'
        }
        $transfer = @{ payloadBytes = $payloadBytes; selectedInterfaceReceivedDelta = ($afterCounter - $beforeCounter); monitoredReceivedDelta = $monitoredDelta }
        $latest = $after
    }
    if (-not $SkipRestart) {
        $hiveProcess.Kill()
        $hiveProcess.WaitForExit()
        Start-Sleep -Seconds 6
        $restarted = [datetime]::UtcNow
        $hiveProcess = Start-Monitor $hiveExecutable $hiveVariables 'hive-2'
        $reconnected = Wait-Node $restarted
        if ($reconnected.totals.totalBytes -lt $latest.totals.totalBytes) { throw 'Totals regressed after Hive restart.' }
        $beeProcess.Kill()
        $beeProcess.WaitForExit()
        Start-Sleep -Seconds 2
        $restarted = [datetime]::UtcNow
        $beeProcess = Start-Monitor $beeExecutable $beeVariables 'bee-2'
        $latest = Wait-Node $restarted
        if ($latest.totals.totalBytes -lt $reconnected.totals.totalBytes -or -not $latest.totals.hasGaps) {
            throw 'Bee restart did not preserve totals and report its observation gap.'
        }
    }
    $from = [Uri]::EscapeDataString($started.AddSeconds(-5).ToString('O'))
    $to = [Uri]::EscapeDataString([datetime]::UtcNow.AddSeconds(5).ToString('O'))
    $history = Invoke-RestMethod "$baseUrl/api/v1/bees/smoke-bee/traffic/history?from=$from&to=$to&step=5s" -Headers $headers
    if ($history.points.Count -eq 0) { throw 'History is empty.' }
    $report = [ordered]@{
        passed = $true; capturedAt = [datetime]::UtcNow.ToString('O'); hiveExecutable = $hiveExecutable
        beeExecutable = $beeExecutable; platform = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        warmupSeconds = $WarmupSeconds; observationSeconds = $ObservationSeconds; beeCpuPercentOfOneCore = [Math]::Round($cpuCorePercent, 3)
        beeWorkingSetMiB = [Math]::Round($workingSetMiB, 2); current = $current
        finalTotals = $latest.totals; interfaces = $latest.node.interfaces; historyPoints = $history.points.Count
        restartChecks = (-not $SkipRestart); transfer = $transfer; note = 'Short smoke measurement, not a production performance benchmark.'
    }
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $resultDirectory 'result.json') -Encoding utf8
    $report | ConvertTo-Json -Depth 12
} finally {
    foreach ($entry in $startedProcesses) {
        if (-not $entry.Process.HasExited) { $entry.Process.Kill(); $entry.Process.WaitForExit() }
        $entry.Out.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $runDirectory "$($entry.Label).stdout.log")
        $entry.Error.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $runDirectory "$($entry.Label).stderr.log")
        $entry.Process.Dispose()
    }
}
