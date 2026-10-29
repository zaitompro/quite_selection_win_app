[CmdletBinding()]
param([switch]$Installed, [switch]$PrepareFromApprovedCache, [switch]$PlayTest, [switch]$KittenOnly, [switch]$PocketOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools/dotnet'
$reportName = if ($PocketOnly) { 'pocket' } elseif ($KittenOnly) { 'kitten-mini' } else { 'additional-speech' }
$report = Join-Path $projectRoot ('artifacts/test-results/' + $reportName + $(if ($Installed) { '-installed' } else { '' }))
New-Item -ItemType Directory -Path $report -Force | Out-Null
$started = [DateTime]::UtcNow
$testFlag = '--additional-speech-tests'
$arguments = $testFlag + ' --report-directory "' + $report + '"'
if ($PocketOnly) {
    $arguments += ' --pocket-only'
    if (-not $Installed) { $arguments += ' --model-cache-root "' + (Join-Path $projectRoot '.tools') + '"' }
    elseif ($PrepareFromApprovedCache) { $arguments += ' --prepare-pocket-cache "' + (Join-Path $projectRoot '.tools/pocket-tts-downloads') + '"' }
} elseif ($KittenOnly) {
    $arguments += ' --kitten-only'
    if (-not $Installed) { $arguments += ' --model-cache-root "' + (Join-Path $projectRoot '.tools') + '"' }
    elseif ($PrepareFromApprovedCache) { $arguments += ' --prepare-kitten-cache "' + (Join-Path $projectRoot '.tools/kitten-mini-downloads') + '"' }
} elseif ($PrepareFromApprovedCache) { $arguments += ' --prepare-kokoro-cache "' + (Join-Path $projectRoot '.tools/kokoro-downloads') + '" --prepare-voicevox-cache "' + (Join-Path $projectRoot '.tools/voicevox-downloads/0.16.4') + '" --prepare-kitten-cache "' + (Join-Path $projectRoot '.tools/kitten-mini-downloads') + '"' }
if ($PlayTest) { $arguments += ' --play-additional-test' }
if ($Installed) {
    $launcher = Join-Path $projectRoot 'src/QuietSelection.Setup/bin/Release/net10.0-windows/QuietSelection.Setup.dll'
    $result = & (Join-Path $env:DOTNET_ROOT 'dotnet.exe') $launcher launch $arguments
    if ($LASTEXITCODE -ne 0 -or $result -notmatch '^QS_PACKAGE_ACTIVATED (\d+)$') { throw 'ADDITIONAL_SPEECH_TEST_ACTIVATION_FAILED' }
    $process = Get-Process -Id ([int]$Matches[1])
} else {
    $process = Start-Process -FilePath (Join-Path $projectRoot 'src/QuietSelection.App/bin/Release/net10.0-windows/QuietSelection.exe') -ArgumentList $arguments -PassThru -WindowStyle Hidden
}
if (-not $process.WaitForExit(1200000)) { throw 'ADDITIONAL_SPEECH_TEST_TIMEOUT' }
$file = Join-Path $report 'additional-speech.json'
if (-not (Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).LastWriteTimeUtc -lt $started) { throw 'ADDITIONAL_SPEECH_FRESH_REPORT_MISSING' }
$r = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
if (-not $r.complete) { throw 'ADDITIONAL_SPEECH_INCOMPLETE_REPORT' }
[pscustomobject]@{Report=$file;Total=$r.checks.Count;Passed=@($r.checks | Where-Object passed).Count;Packaged=$r.packaged;Failures=@($r.checks | Where-Object { -not $_.passed })} | ConvertTo-Json -Depth 4
if (@($r.checks | Where-Object { -not $_.passed }).Count -gt 0 -or ($Installed -and -not $r.packaged)) { throw 'ADDITIONAL_SPEECH_TEST_FAILED' }

