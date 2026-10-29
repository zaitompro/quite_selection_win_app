[CmdletBinding()]
param([switch]$Tray)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools/dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$binary = Join-Path $projectRoot 'src/QuietSelection.App/bin/Release/net10.0-windows/QuietSelection.exe'
if ($Tray) { Start-Process -FilePath $binary -ArgumentList '--tray' -WindowStyle Hidden }
else { Start-Process -FilePath $binary -WindowStyle Normal }
