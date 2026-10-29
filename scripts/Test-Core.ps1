$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& (Join-Path $projectRoot '.tools/dotnet/dotnet.exe') (Join-Path $projectRoot 'tests/QuietSelection.Core.Tests/bin/Release/net10.0/QuietSelection.Core.Tests.dll')
if ($LASTEXITCODE -ne 0) { throw 'CORE_TESTS_FAILED' }
