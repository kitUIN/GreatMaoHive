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
    Updates__Enabled = 'false'
    URLS = $baseUrl
    Hive__DatabasePath = (Join-Path $runDirectory 'hive.db')
    Hive__ReadApiKey = $readKey
    Hive__Bees__0__Id = 'smoke-bee'
    Hive__Bees__0__Token = $beeKey
    Hive__AllowInsecureTransport = 'true'
    Hive__Bees__0__Quota__LimitBytes = '1'
    Hive__Bees__0__Quota__Period = 'monthly'
    Hive__Bees__0__Quota__UtcOffsetHours = '8'
}
$beeVariables = @{
    Updates__Enabled = 'false'
    Bee__Id = 'smoke-bee'
    Bee__Name = 'Smoke Bee'
    Bee__Region = 'Hong Kong'
    Bee__Ip = '2001:db8::10'
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
    if ($first.node.region -ne $beeVariables.Bee__Region -or $first.node.ip -ne $beeVariables.Bee__Ip) {
        throw 'Bee region/IP configuration was not reported in node details.'
    }
    $swagger = Invoke-WebRequest "$baseUrl/swagger/index.html" -TimeoutSec 10
    if ($swagger.Content -notmatch 'SwaggerUIBundle') { throw 'Embedded Swagger UI is missing.' }
    $openapi = Invoke-RestMethod "$baseUrl/swagger/v1/swagger.json" -TimeoutSec 10
    if ($null -eq $openapi.paths.'/api/v1/bees'.get -or $openapi.components.securitySchemes.Bearer.scheme -ne 'bearer') {
        throw 'OpenAPI document or Bearer authentication scheme is missing.'
    }
    foreach ($asset in @('swagger-ui.css', 'swagger-ui-bundle.js')) {
        $response = Invoke-WebRequest "$baseUrl/swagger/$asset" -TimeoutSec 10
        if ($response.RawContentLength -lt 1000) { throw "Swagger asset is missing: $asset" }
    }
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
        if ($reconnected.node.region -ne $beeVariables.Bee__Region -or $reconnected.node.ip -ne $beeVariables.Bee__Ip) {
            throw 'Bee region/IP was lost after Hive restart.'
        }
        $beeProcess.Kill()
        $beeProcess.WaitForExit()
        Start-Sleep -Seconds 2
        $restarted = [datetime]::UtcNow
        $beeVariables.Bee__Region = 'Tokyo'
        $beeVariables.Bee__Ip = '192.0.2.10'
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
    $quotas = Invoke-RestMethod "$baseUrl/api/v1/bees/traffic/quotas" -Headers $headers
    $quota = @($quotas.bees | Where-Object { $_.id -eq 'smoke-bee' })
    $summaries = Invoke-RestMethod "$baseUrl/api/v1/bees" -Headers $headers
    $summary = @($summaries | Where-Object { $_.id -eq 'smoke-bee' })
    if ($summary.Count -ne 1 -or $quota.Count -ne 1 -or $summary[0].region -ne $beeVariables.Bee__Region -or $summary[0].ip -ne $beeVariables.Bee__Ip -or
        $latest.node.region -ne $beeVariables.Bee__Region -or $latest.node.ip -ne $beeVariables.Bee__Ip -or
        $quota[0].region -ne $beeVariables.Bee__Region -or $quota[0].ip -ne $beeVariables.Bee__Ip) {
        throw 'Bee region/IP was not preserved in node list, details and quota queries.'
    }
    if ($quota.Count -ne 1 -or $quota[0].quota.limitBytes -ne 1 -or $quota[0].quota.utcOffsetHours -ne 8 -or $quota[0].totalBytes -le 0 -or -not $quota[0].isExceeded) {
        throw 'Period quota configuration or traffic aggregation failed.'
    }
    $onlineTo = [Uri]::EscapeDataString([datetimeoffset]::UtcNow.ToString('O'))
    $onlineHistory = Invoke-RestMethod "$baseUrl/api/v1/bees/smoke-bee/online/history?from=$from&to=$onlineTo" -Headers $headers
    if ($onlineHistory.onlineSeconds -le 0 -or @($onlineHistory.intervals).Count -eq 0) { throw 'Online history was not persisted.' }
    if (-not $SkipRestart -and $onlineHistory.unknownSeconds -le 0) { throw 'Hive downtime should be unknown in online history.' }
    $report = [ordered]@{
        passed = $true; capturedAt = [datetime]::UtcNow.ToString('O'); hiveExecutable = $hiveExecutable
        beeExecutable = $beeExecutable; platform = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        warmupSeconds = $WarmupSeconds; observationSeconds = $ObservationSeconds; beeCpuPercentOfOneCore = [Math]::Round($cpuCorePercent, 3)
        beeWorkingSetMiB = [Math]::Round($workingSetMiB, 2); current = $current
        finalTotals = $latest.totals; interfaces = $latest.node.interfaces; historyPoints = $history.points.Count
        restartChecks = (-not $SkipRestart); swagger = $true; quota = $quota[0]; onlineHistory = $onlineHistory
        transfer = $transfer; note = 'Short smoke measurement, not a production performance benchmark.'
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
