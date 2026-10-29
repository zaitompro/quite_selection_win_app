[CmdletBinding()]
param([ValidateSet('ru','en')][string[]]$Languages = @('ru','en'))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $projectRoot '.tools/voice-downloads'
foreach ($language in $Languages) {
    & (Join-Path $projectRoot '.tools/dotnet/dotnet.exe') (Join-Path $projectRoot 'src/QuietSelection.Setup/bin/Release/net10.0-windows/QuietSelection.Setup.dll') rhvoice $language $directory --install
    if ($LASTEXITCODE -ne 0) { throw 'RHVOICE_INSTALL_FAILED' }
}
