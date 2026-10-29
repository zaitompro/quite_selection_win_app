[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$SkipNative)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools/dotnet'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/nuget'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'RUN_Install-ProjectSdk.ps1_FIRST_WITH_CONSENT' }
if (-not $SkipNative) { & (Join-Path $PSScriptRoot 'Build-Native.ps1') -Configuration $Configuration }
Push-Location $projectRoot
try {
    & $dotnet build QuietSelection.slnx -c $Configuration --configfile NuGet.Config -m:1 -p:UseSharedCompilation=false --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'MANAGED_BUILD_FAILED' }
} finally { Pop-Location }
