[CmdletBinding()]
param([switch]$Installed, [switch]$PlayEnglishTest, [switch]$RequireRHVoice, [switch]$PlayRHVoiceTest, [switch]$ManualStart)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools/dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$binary = Join-Path $projectRoot 'src/QuietSelection.App/bin/Release/net10.0-windows/QuietSelection.exe'
$startedAt = [DateTime]::UtcNow
# The test window contains only synthetic text. No existing app is inspected.
$reportDirectory = Join-Path $projectRoot $(if ($Installed) { 'artifacts/test-results/installed' } else { 'artifacts/test-results/unpackaged' })
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$arguments = '--api-tests --report-directory "' + $reportDirectory + '"'
if ($PlayEnglishTest) { $arguments += ' --play-english-test' }
if ($RequireRHVoice) { $arguments += ' --require-rhvoice --rhvoice-cache "' + (Join-Path $projectRoot '.tools/voice-downloads') + '"' }
if ($PlayRHVoiceTest) { $arguments += ' --play-rhvoice-test' }
if ($ManualStart) { $arguments += ' --manual-start' }
if ($Installed) {
    $launcher = Join-Path $projectRoot 'src/QuietSelection.Setup/bin/Release/net10.0-windows/QuietSelection.Setup.dll'
    $launchResult = & (Join-Path $env:DOTNET_ROOT 'dotnet.exe') $launcher launch $arguments
    if ($LASTEXITCODE -ne 0 -or $launchResult -notmatch '^QS_PACKAGE_ACTIVATED (\d+)$') { throw 'PACKAGE_ACTIVATION_FAILED' }
    $testProcess = Get-Process -Id ([int]$Matches[1])
} else {
    $testProcess = Start-Process -FilePath $binary -ArgumentList $arguments -PassThru -WindowStyle Hidden
}
if (-not $testProcess.WaitForExit(300000)) { throw 'WINDOWS_API_TEST_TIMEOUT' }
$result = Join-Path $reportDirectory 'windows-api.json'
if (-not (Test-Path -LiteralPath $result) -or (Get-Item -LiteralPath $result).LastWriteTimeUtc -lt $startedAt) { throw 'FRESH_WINDOWS_API_REPORT_NOT_CREATED' }
Get-Content -LiteralPath $result -Raw
$report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
if (@($report.checks | Where-Object { -not $_.passed }).Count -gt 0) { throw 'WINDOWS_API_TESTS_FAILED' }
if ($Installed -and -not $report.packaged) { throw 'PACKAGE_IDENTITY_MISSING' }
