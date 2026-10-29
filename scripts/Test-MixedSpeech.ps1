[CmdletBinding()]
param([switch]$Installed)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools/dotnet'
$reportDirectory = Join-Path $projectRoot $(if ($Installed) { 'artifacts/test-results/mixed-installed' } else { 'artifacts/test-results/mixed' })
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$startedAt = [DateTime]::UtcNow
$arguments = '--mixed-speech-tests --report-directory "' + $reportDirectory + '"'
if ($Installed) {
    $launcher = Join-Path $projectRoot 'src/QuietSelection.Setup/bin/Release/net10.0-windows/QuietSelection.Setup.dll'
    $result = & (Join-Path $env:DOTNET_ROOT 'dotnet.exe') $launcher launch $arguments
    if ($LASTEXITCODE -ne 0 -or $result -notmatch '^QS_PACKAGE_ACTIVATED (\d+)$') { throw 'PACKAGE_ACTIVATION_FAILED' }
    $testProcess = Get-Process -Id ([int]$Matches[1])
} else {
    $arguments += ' --model-cache-root "' + (Join-Path $projectRoot '.tools') + '"'
    $testProcess = Start-Process -FilePath (Join-Path $projectRoot 'src/QuietSelection.App/bin/Release/net10.0-windows/QuietSelection.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
}
if (-not $testProcess.WaitForExit(780000)) { throw 'MIXED_SPEECH_TEST_TIMEOUT' }
$reportFile = Join-Path $reportDirectory 'mixed-speech.json'
if (-not (Test-Path -LiteralPath $reportFile) -or (Get-Item -LiteralPath $reportFile).LastWriteTimeUtc -lt $startedAt) { throw 'FRESH_MIXED_REPORT_MISSING' }
$report = Get-Content -LiteralPath $reportFile -Raw | ConvertFrom-Json
$report.checks | Format-Table
if (-not $report.complete -or @($report.checks | Where-Object { -not $_.passed }).Count -gt 0) { throw 'MIXED_SPEECH_TEST_FAILED' }
if ($Installed -and -not $report.packaged) { throw 'PACKAGE_IDENTITY_MISSING' }
