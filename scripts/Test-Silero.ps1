[CmdletBinding()]
param([switch]$Installed, [switch]$PrepareFromApprovedCache, [switch]$PlayTest, [switch]$Smoke, [switch]$TextOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools/dotnet'
$report = Join-Path $projectRoot $(if ($Installed) { 'artifacts/test-results/silero-installed' } else { 'artifacts/test-results/silero' })
New-Item -ItemType Directory -Path $report -Force | Out-Null
$started = [DateTime]::UtcNow
$testFlag = '--silero-tests'
$arguments = $testFlag + ' --report-directory "' + $report + '"'
if ($PrepareFromApprovedCache) { $arguments += ' --prepare-silero-cache "' + (Join-Path $projectRoot '.tools/silero-downloads') + '"' }
if ($PlayTest) { $arguments += ' --play-silero-test' }
if ($Smoke) { $arguments += ' --silero-smoke' }
if ($TextOnly) { $arguments += ' --silero-text-only' }
if ($Installed) {
    $launcher = Join-Path $projectRoot 'src/QuietSelection.Setup/bin/Release/net10.0-windows/QuietSelection.Setup.dll'
    $result = & (Join-Path $env:DOTNET_ROOT 'dotnet.exe') $launcher launch $arguments
    if ($LASTEXITCODE -ne 0 -or $result -notmatch '^QS_PACKAGE_ACTIVATED (\d+)$') { throw 'SILERO_TEST_ACTIVATION_FAILED' }
    $process = Get-Process -Id ([int]$Matches[1])
} else {
    $process = Start-Process -FilePath (Join-Path $projectRoot 'src/QuietSelection.App/bin/Release/net10.0-windows/QuietSelection.exe') -ArgumentList $arguments -PassThru -WindowStyle Hidden
}
if (-not $process.WaitForExit(1200000)) { throw 'SILERO_TEST_TIMEOUT' }
$file = Join-Path $report 'silero.json'
if (-not (Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).LastWriteTimeUtc -lt $started) { throw 'SILERO_FRESH_REPORT_MISSING' }
$r = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
if (-not $r.complete) { throw 'SILERO_INCOMPLETE_REPORT' }
[pscustomobject]@{Report=$file;Total=$r.checks.Count;Passed=@($r.checks | Where-Object passed).Count;Packaged=$r.packaged;Failures=@($r.checks | Where-Object { -not $_.passed })} | ConvertTo-Json -Depth 4
if (@($r.checks | Where-Object { -not $_.passed }).Count -gt 0 -or ($Installed -and -not $r.packaged)) { throw 'SILERO_TEST_FAILED' }
