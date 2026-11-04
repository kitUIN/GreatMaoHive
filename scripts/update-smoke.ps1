param([Parameter(Mandatory)][ValidateSet('win-x64','linux-x64')][string]$Runtime)
$ErrorActionPreference = 'Stop'
$suffix = if ($Runtime -eq 'win-x64') { '.exe' } else { '' }
$root = [IO.Path]::GetFullPath("artifacts/verification/update-$([Guid]::NewGuid().ToString('N'))")
$install = Join-Path $root 'install'
$state = Join-Path $root 'updates'
$stage = Join-Path $state 'stage'
$payload = Join-Path $stage 'payload'
New-Item -ItemType Directory -Force $install, $payload | Out-Null
Copy-Item "artifacts/publish/GreatMaoNetHive/$Runtime/*" $install
Copy-Item "$install/*" $payload
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$baseUrl = "http://127.0.0.1:$port"
$key = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$beeKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$arguments = @('--urls', $baseUrl, '--Updates:Enabled', 'false', '--Hive:AllowInsecureTransport', 'true',
    '--Hive:ReadApiKey', $key, '--Hive:Bees:0:Token', $beeKey, '--Hive:DatabasePath', (Join-Path $install 'data/hive.db'))
$config = '{"UpdateSmoke":"preserve-local-configuration"}'
[IO.File]::WriteAllText((Join-Path $install 'appsettings.Local.json'), $config)
$applicationPath = Join-Path $install "GreatMaoNetHive$suffix"
$initial = $null
$updater = $null
function Wait-Health {
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        try {
            if ((Invoke-RestMethod "$baseUrl/healthz" -TimeoutSec 1).status -eq 'ok') { return }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    throw 'Updated Hive did not become healthy.'
}
try {
    $info = [Diagnostics.ProcessStartInfo]::new($applicationPath)
    $info.WorkingDirectory = $install
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    $initial = [Diagnostics.Process]::Start($info)
    Wait-Health
    $files = @(Get-ChildItem $payload -File | Where-Object {
        $_.Name -eq "GreatMaoNetHive$suffix" -or $_.Name -eq "GreatMaoNetUpdater$suffix" -or
        $_.Name -match '\.(dll|so|deps\.json|runtimeconfig\.json)$'
    } | ForEach-Object Name)
    $plan = @{
        Application = 'GreatMaoNetHive'; Version = '99.0.0'; PayloadDirectory = $payload; Files = $files
        WorkingDirectory = $install; Arguments = $arguments; ParentId = $initial.Id
        ParentStartTicks = $initial.StartTime.ToUniversalTime().Ticks; Mode = 'console'
    }
    $plan | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $state 'pending.json') -Encoding utf8NoBOM
    # Use a separate native helper, as the running application does during a real update.
    Copy-Item (Join-Path $install "GreatMaoNetUpdater$suffix") (Join-Path $stage "GreatMaoNetUpdater$suffix")
    $helperInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $stage "GreatMaoNetUpdater$suffix"))
    $helperInfo.UseShellExecute = $false
    $helperInfo.CreateNoWindow = $true
    $helperInfo.ArgumentList.Add($install)
    $helperInfo.ArgumentList.Add($state)
    $updater = [Diagnostics.Process]::Start($helperInfo)
    for ($attempt = 0; -not (Test-Path (Join-Path $stage 'ready')); $attempt++) {
        if ($updater.HasExited -or $attempt -ge 100) { throw 'Updater failed to become ready.' }
        Start-Sleep -Milliseconds 100
    }
    if ($initial.HasExited) { throw 'Original process exited before update handoff.' }
    $initial.Kill()
    $initial.WaitForExit()
    if (-not $updater.WaitForExit(30000) -or $updater.ExitCode -ne 0) { throw 'Updater failed.' }
    Wait-Health
    if ((Get-Content -Raw (Join-Path $install 'appsettings.Local.json')) -ne $config) { throw 'Local configuration was overwritten.' }
    if (-not (Test-Path (Join-Path $install 'data/hive.db'))) { throw 'Database was removed.' }
    if (-not (Test-Path (Join-Path $state 'last-update.json'))) { throw 'Update completion was not recorded.' }
    @{ passed = $true; runtime = $Runtime; checked = @('parent handoff', 'binary replacement', 'restart', 'HTTP health', 'configuration/data preservation') } |
        ConvertTo-Json | Set-Content (Join-Path $root 'result.json') -Encoding utf8NoBOM
} finally {
    if ($null -ne $initial -and -not $initial.HasExited) { $initial.Kill(); $initial.WaitForExit() }
    if ($null -ne $updater -and -not $updater.HasExited) { $updater.Kill(); $updater.WaitForExit() }
    # Only stop processes running the executable in this test's unique directory.
    Get-Process -Name GreatMaoNetHive -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $applicationPath } |
        ForEach-Object { $_.Kill(); $_.WaitForExit() }
}
