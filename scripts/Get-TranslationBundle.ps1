[CmdletBinding()]
param([switch]$ConfirmDownload)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmDownload) { throw 'Explicit consent required: 357540189 bytes; see docs/translation-proposal.md.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$downloadRoot = Join-Path $projectRoot '.tools/translation-downloads'
New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
$items = @(
    @{Name='translate-en_ru-1_9.argosmodel'; Url='https://argos-net.com/v1/translate-en_ru-1_9.argosmodel'; Size=195746693; Hash=''},
    @{Name='translate-ja_en-1_1.argosmodel'; Url='https://argos-net.com/v1/translate-ja_en-1_1.argosmodel'; Size=117155716; Hash=''},
    @{Name='python-3.13.16-embed-amd64.zip'; Url='https://www.python.org/ftp/python/3.13.16/python-3.13.16-embed-amd64.zip'; Size=11439094; Hash='97dae5274cc54867065e8d5a3226e48c35017ed332a0fdb0e27d5b5821961297'},
    @{Name='ctranslate2-4.8.3-cp313-cp313-win_amd64.whl'; Project='ctranslate2'; Version='4.8.3'; Size=19230496; Hash='c3c5d19b83df19f9f708ed16145fbc20b06827462f1a68c5286efc0ad41aa0c1'},
    @{Name='sentencepiece-0.2.2-cp313-cp313-win_amd64.whl'; Project='sentencepiece'; Version='0.2.2'; Size=1247987; Hash='74f0ee601047c0c12a783088b51be4e6214a62ecd9e02278c477433cd16e0ed9'},
    @{Name='numpy-2.5.4-cp313-cp313-win_amd64.whl'; Project='numpy'; Version='2.5.4'; Size=12566113; Hash='8b4d2fd2d34e5f8c9235ee787de5631a37a28402b15cb80814df973d2be54129'},
    @{Name='pyyaml-6.0.3-cp313-cp313-win_amd64.whl'; Project='PyYAML'; Version='6.0.3'; Size=154090; Hash='79005a0d97d5ddabfeeea4cf676af11e647e41d81c9a7722a193022accdb6b7c'}
)
$receipts = @()
foreach ($item in $items) {
    if ($item.Project) {
        $metadata = Invoke-RestMethod ('https://pypi.org/pypi/' + $item.Project + '/' + $item.Version + '/json')
        $file = @($metadata.urls | Where-Object filename -CEQ $item.Name)
        if ($file.Count -ne 1 -or $file[0].size -ne $item.Size -or $file[0].digests.sha256 -ne $item.Hash) { throw 'PUBLISHED_METADATA_MISMATCH' }
        $item.Url = $file[0].url
    }
    $destination = Join-Path $downloadRoot $item.Name
    if (-not (Test-Path -LiteralPath $destination)) {
        Write-Output ('Downloading: ' + $item.Name + ' (' + $item.Size + ' bytes)')
        Invoke-WebRequest -Uri $item.Url -OutFile ($destination + '.partial')
        if ((Get-Item -LiteralPath ($destination + '.partial')).Length -ne $item.Size) { throw 'DOWNLOAD_SIZE_MISMATCH' }
        $hash = (Get-FileHash -LiteralPath ($destination + '.partial') -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($item.Hash -and $hash -ne $item.Hash) { throw 'DOWNLOAD_HASH_MISMATCH' }
        Move-Item -LiteralPath ($destination + '.partial') -Destination $destination
    }
    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-Item -LiteralPath $destination).Length -ne $item.Size -or ($item.Hash -and $hash -ne $item.Hash)) { throw 'CACHED_DOWNLOAD_INVALID' }
    $receipts += [pscustomobject]@{name=$item.Name; bytes=$item.Size; sha256=$hash; publisherHashVerified=[bool]$item.Hash; source=$item.Url}
    Write-Output ('Verified: ' + $item.Name)
}
$receipts | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $downloadRoot 'receipts.json') -Encoding utf8
Write-Output 'BUNDLE_DOWNLOAD_COMPLETE'
