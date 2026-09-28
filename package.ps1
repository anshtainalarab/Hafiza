$ErrorActionPreference = 'Stop'
$workspace = $PSScriptRoot
$dotnet = Join-Path $workspace '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
$compiler = Join-Path $workspace '.tools\InnoSetup\ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Inno Setup compiler was not found at .tools\InnoSetup\ISCC.exe.'
}
$env:DOTNET_CLI_HOME = Join-Path $workspace '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $dotnet publish (Join-Path $workspace 'Hafiza\Hafiza.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $workspace 'Release\Hafiza-Standalone')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $compiler (Join-Path $workspace 'installer.iss')
exit $LASTEXITCODE
