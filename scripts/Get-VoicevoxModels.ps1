[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $projectRoot 'third-party/voicevox/models.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
if ($catalog.Version -ne '0.16.4') { throw 'UNSUPPORTED_VOICEVOX_MODEL_RELEASE' }
$downloads = Join-Path $projectRoot '.tools/voicevox-downloads/0.16.4'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
foreach ($item in $catalog.Files) {
    if ($item.Name -notin @('0.vvm','4.vvm','README.txt','TERMS.txt')) { throw 'VOICEVOX_ASSET_NOT_SELECTED' }
    $expectedUrl = 'https://github.com/VOICEVOX/voicevox_vvm/releases/download/0.16.4/' + $item.Name
    if ($item.Url -ne $expectedUrl -or $item.Sha256 -notmatch '^[a-f0-9]{64}$') { throw 'VOICEVOX_CATALOG_INVALID' }
    $path = Join-Path $downloads $item.Name
    if ((Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).Length -eq $item.Bytes -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $item.Sha256) {
        Write-Output ('REUSED ' + $item.Name); continue
    }
    $partial = $path + '.partial'
    & curl.exe --ipv4 --http1.1 --location --fail --silent --show-error --proto '=https' --proto-redir '=https' --connect-timeout 30 --max-time 900 --speed-limit 1024 --speed-time 45 --retry 2 --retry-delay 2 --max-filesize $item.Bytes --output $partial $item.Url
    if ($LASTEXITCODE -ne 0) { throw ('VOICEVOX_DOWNLOAD_FAILED ' + $item.Name) }
    if ((Get-Item -LiteralPath $partial).Length -ne $item.Bytes -or (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $item.Sha256) { throw 'VOICEVOX_DOWNLOAD_INTEGRITY_FAILED' }
    Move-Item -LiteralPath $partial -Destination $path -Force
    Write-Output ('VERIFIED ' + $item.Name + ' ' + $item.Bytes)
}
# Keep the publisher's notices next to the small, source-controlled catalog.
foreach ($name in @('README.txt','TERMS.txt')) {
    Copy-Item -LiteralPath (Join-Path $downloads $name) -Destination (Join-Path $projectRoot ('third-party/voicevox/' + $name))
}
Write-Output ('TOTAL_MODEL_BYTES ' + (($catalog.Files | Where-Object Name -like '*.vvm' | Measure-Object Bytes -Sum).Sum))
