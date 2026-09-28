$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'Hafiza\Hafiza.csproj'
$localDotnet = Join-Path $PSScriptRoot '.dotnet\dotnet.exe'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if (Test-Path -LiteralPath $localDotnet) {
    & $localDotnet build $projectPath -c Release
} else {
    dotnet build $projectPath -c Release
}
