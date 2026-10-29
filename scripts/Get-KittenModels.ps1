[CmdletBinding()]
param([switch]$ConfirmDownload)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmDownload) { throw 'DOWNLOAD_CONSENT_REQUIRED' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $projectRoot '.tools/kitten-mini-downloads'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$catalog = Get-Content (Join-Path $projectRoot 'src/QuietSelection.App/Speech/Downloads/kitten-models.json') -Raw | ConvertFrom-Json
$report = @()
# Deliberately sequential: never start another transfer until verification finishes.
foreach ($item in $catalog.Files) {
    $path = Join-Path $cache $item.Name
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Output ('DOWNLOADING ' + $item.Name)
        & curl.exe --ipv4 --http1.1 --location --fail --silent --show-error --proto '=https' --proto-redir '=https' --connect-timeout 30 --max-time 1800 --speed-limit 1024 --speed-time 45 --retry 2 --retry-delay 2 --max-filesize $item.Bytes --output ($path + '.partial') $item.Url
        if ($LASTEXITCODE -ne 0) { throw 'KITTEN_DOWNLOAD_FAILED' }
        if ((Get-Item -LiteralPath ($path + '.partial')).Length -ne $item.Bytes -or (Get-FileHash -LiteralPath ($path + '.partial')).Hash.ToLowerInvariant() -ne $item.Sha256) { throw 'KITTEN_INTEGRITY_FAILED' }
        Move-Item -LiteralPath ($path + '.partial') -Destination $path
    }
    if ((Get-Item -LiteralPath $path).Length -ne $item.Bytes -or (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $item.Sha256) { throw 'KITTEN_INTEGRITY_FAILED' }
    $report += [ordered]@{ Name=$item.Name; Bytes=$item.Bytes; Sha256=$item.Sha256; Url=$item.Url }
    Write-Output ('VERIFIED ' + $item.Name)
}
New-Item -ItemType Directory -Path (Join-Path $projectRoot 'artifacts/test-results') -Force | Out-Null
$report | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $projectRoot 'artifacts/test-results/kitten-mini-downloads.json')
