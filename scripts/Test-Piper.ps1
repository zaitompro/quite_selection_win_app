[CmdletBinding()]
param([switch]$Installed, [switch]$PrepareFromApprovedCache, [switch]$PlayTest, [switch]$TestDownload, [switch]$Resident)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools/dotnet'
$report = Join-Path $projectRoot $(if ($Installed) { 'artifacts/test-results/piper-installed' } else { 'artifacts/test-results/piper' })
New-Item -ItemType Directory -Path $report -Force | Out-Null
$started = [DateTime]::UtcNow
$testFlag = if ($Resident) { '--piper-resident-tests' } else { '--piper-tests' }
$arguments = $testFlag + ' --report-directory "' + $report + '"'
if ($PrepareFromApprovedCache) { $arguments += ' --prepare-piper-cache "' + (Join-Path $projectRoot '.tools/piper-downloads') + '"' }
if ($PlayTest) { $arguments += ' --play-piper-test' }
if ($TestDownload) { $arguments += ' --test-piper-download' }
if ($Installed) {
    $launcher = Join-Path $projectRoot 'src/QuietSelection.Setup/bin/Release/net10.0-windows/QuietSelection.Setup.dll'
    $result = & (Join-Path $env:DOTNET_ROOT 'dotnet.exe') $launcher launch $arguments
    if ($LASTEXITCODE -ne 0 -or $result -notmatch '^QS_PACKAGE_ACTIVATED (\d+)$') { throw 'PIPER_TEST_ACTIVATION_FAILED' }
    $process = Get-Process -Id ([int]$Matches[1])
} else {
    $process = Start-Process -FilePath (Join-Path $projectRoot 'src/QuietSelection.App/bin/Release/net10.0-windows/QuietSelection.exe') -ArgumentList $arguments -PassThru -WindowStyle Hidden
}
if (-not $process.WaitForExit(600000)) { throw 'PIPER_TEST_TIMEOUT' }
$file = Join-Path $report $(if ($Resident) { 'piper-resident.json' } else { 'piper.json' })
if (-not (Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).LastWriteTimeUtc -lt $started) { throw 'PIPER_FRESH_REPORT_MISSING' }
$r = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
[pscustomobject]@{Report=$file;Total=$r.checks.Count;Passed=@($r.checks | Where-Object passed).Count;Packaged=$r.packaged;Failures=@($r.checks | Where-Object { -not $_.passed })} | ConvertTo-Json -Depth 4
if (@($r.checks | Where-Object { -not $_.passed }).Count -gt 0 -or ($Installed -and -not $r.packaged)) { throw 'PIPER_TEST_FAILED' }
