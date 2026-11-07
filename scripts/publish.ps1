param(
    [ValidateSet('win-x64','linux-x64')][string]$Runtime = 'win-x64',
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Version = '0.2.0'
)
$ErrorActionPreference = 'Stop'
if ($Runtime -eq 'linux-x64') {
    docker build --file scripts/linux-build.Dockerfile --build-arg "VERSION=$Version" --target export --output type=local,dest=artifacts/publish .
    if ($LASTEXITCODE -ne 0) { throw 'Debian 11 compatible Native AOT publish failed' }
    return
}
dotnet publish src/GreatMaoNetUpdater/GreatMaoNetUpdater.csproj -c Release -r $Runtime "-p:Version=$Version" -o "artifacts/publish/GreatMaoNetUpdater/$Runtime"
if ($LASTEXITCODE -ne 0) { throw 'Native AOT publish failed for updater' }
$updaterName = if ($Runtime -eq 'win-x64') { 'GreatMaoNetUpdater.exe' } else { 'GreatMaoNetUpdater' }
foreach ($application in @('GreatMaoNetHive','GreatMaoNetBee')) {
    dotnet publish "src/$application/$application.csproj" -c Release -r $Runtime "-p:Version=$Version" -o "artifacts/publish/$application/$Runtime"
    if ($LASTEXITCODE -ne 0) { throw "Native AOT publish failed for $application" }
    Copy-Item -LiteralPath "artifacts/publish/GreatMaoNetUpdater/$Runtime/$updaterName" -Destination "artifacts/publish/$application/$Runtime/$updaterName" -Force
}
