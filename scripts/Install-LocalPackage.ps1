[CmdletBinding()]
param([switch]$ConfirmLocalPublisherTrust)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmLocalPublisherTrust) { throw 'EXPLICIT_CURRENT_USER_TRUST_CONSENT_REQUIRED' }
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'PackageArtifacts.ps1')
& (Join-Path $projectRoot '.tools/dotnet/dotnet.exe') (Join-Path $projectRoot 'src/QuietSelection.Setup/bin/Release/net10.0-windows/QuietSelection.Setup.dll') install $projectRoot --confirm-current-user-trust
if ($LASTEXITCODE -ne 0) { throw 'LOCAL_PACKAGE_INSTALLATION_FAILED' }
# Setup writes this receipt only after signing and successful OS installation.
$receipt = Get-Content -LiteralPath (Join-Path $projectRoot 'artifacts/packages/installation.json') -Raw | ConvertFrom-Json
$signedName = 'QuietSelection_' + $receipt.version + '_x64.msix'
Remove-ObsoletePackages -ProjectRoot $projectRoot -KeepFileName $signedName
