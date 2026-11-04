param([ValidateSet('win-x64','linux-x64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
foreach ($application in @('GreatMaoNetHive','GreatMaoNetBee')) {
    dotnet publish "src/$application/$application.csproj" -c Release -r $Runtime -o "artifacts/publish/$application/$Runtime"
    if ($LASTEXITCODE -ne 0) { throw "Native AOT publish failed for $application" }
}
