[CmdletBinding()]
param([switch]$Installed, [switch]$PrepareFromApprovedCache, [string]$ModelCache)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools/dotnet'
$startedAt = [DateTime]::UtcNow
$reportDirectory = Join-Path $projectRoot $(if ($Installed) { 'artifacts/test-results/translation-installed' } else { 'artifacts/test-results/translation' })
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$arguments = '--translation-tests --report-directory "' + $reportDirectory + '"'
if ($ModelCache -and -not $PrepareFromApprovedCache) { throw 'MODEL_CACHE_REQUIRES_PREPARATION_SWITCH' }
if ($PrepareFromApprovedCache) {
    $cache = if ($ModelCache) { [IO.Path]::GetFullPath($ModelCache) } else { Join-Path $projectRoot '.tools/translation-downloads' }
    $arguments += ' --prepare-model-cache "' + $cache + '"'
}
if ($Installed) {
    $launcher = Join-Path $projectRoot 'src/QuietSelection.Setup/bin/Release/net10.0-windows/QuietSelection.Setup.dll'
    $launchResult = & (Join-Path $env:DOTNET_ROOT 'dotnet.exe') $launcher launch $arguments
    if ($LASTEXITCODE -ne 0 -or $launchResult -notmatch '^QS_PACKAGE_ACTIVATED (\d+)$') { throw 'PACKAGE_ACTIVATION_FAILED' }
    $testProcess = Get-Process -Id ([int]$Matches[1])
} else {
    $testProcess = Start-Process -FilePath (Join-Path $projectRoot 'src/QuietSelection.App/bin/Release/net10.0-windows/QuietSelection.exe') -ArgumentList $arguments -PassThru -WindowStyle Hidden
}
if (-not $testProcess.WaitForExit($(if ($PrepareFromApprovedCache) { 900000 } else { 180000 }))) { throw 'TRANSLATION_TEST_TIMEOUT' }
$result = Join-Path $reportDirectory 'translation.json'
if (-not (Test-Path -LiteralPath $result) -or (Get-Item -LiteralPath $result).LastWriteTimeUtc -lt $startedAt) { throw 'FRESH_TRANSLATION_REPORT_NOT_CREATED' }
$report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
$failed = @($report.checks | Where-Object { -not $_.passed })
[pscustomobject]@{Passed=$report.checks.Count-$failed.Count;Total=$report.checks.Count;PeakWorkingSetBytes=$report.peakWorkingSetBytes;CompletedUtc=$report.completedUtc;Report=$result} | ConvertTo-Json -Compress
if ($failed.Count -gt 0) { $failed | ConvertTo-Json; throw 'TRANSLATION_TESTS_FAILED' }
