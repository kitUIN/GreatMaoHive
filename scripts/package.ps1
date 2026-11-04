param([Parameter(Mandatory)][ValidateSet('win-x64','linux-x64')][string]$Runtime)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force artifacts/packages | Out-Null
foreach ($application in @('GreatMaoNetHive', 'GreatMaoNetBee')) {
    $source = "artifacts/publish/$application/$Runtime"
    $name = "$application-$Runtime.zip"
    $archive = "artifacts/packages/$name"
    Compress-Archive -Path "$source/*" -DestinationPath $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$archive.sha256", "$hash  $name`n", [Text.UTF8Encoding]::new($false))
}
