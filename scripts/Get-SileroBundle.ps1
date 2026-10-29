[CmdletBinding()]
param([ValidateSet('ru','en')][string]$Language = 'ru', [switch]$ModelOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$downloads = Join-Path $projectRoot '.tools/silero-downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
$runtime = Get-Content -LiteralPath (Join-Path $projectRoot 'third-party/silero/runtime.json') -Raw | ConvertFrom-Json
$catalogName = if ($Language -eq 'en') { 'silero-english-model.json' } else { 'silero-model.json' }
$model = Get-Content -LiteralPath (Join-Path $projectRoot ('src/QuietSelection.App/Speech/Downloads/' + $catalogName)) -Raw | ConvertFrom-Json
$files = if ($ModelOnly) { @($model.File) } else { @($runtime) + @($model.File) }
foreach ($item in $files) {
    if ([IO.Path]::GetFileName($item.Name) -ne $item.Name -or $item.Name.Contains(':')) { throw 'SILERO_DOWNLOAD_PATH' }
    $uri = [Uri]$item.Url
    if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('models.silero.ai','download-r2.pytorch.org','files.pythonhosted.org')) { throw 'SILERO_DOWNLOAD_HOST' }
    $path = Join-Path $downloads $item.Name
    if ((Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).Length -eq $item.Bytes -and (Get-FileHash -LiteralPath $path).Hash -eq $item.Sha256) {
        Write-Output ('REUSED ' + $item.Name); continue
    }
    $partial = $path + '.partial'
    & curl.exe --http1.1 --fail --silent --show-error --proto '=https' --connect-timeout 30 --max-time 7200 --speed-limit 1024 --speed-time 120 --max-filesize $item.Bytes --output $partial $item.Url
    if ($LASTEXITCODE -ne 0 -or (Get-Item -LiteralPath $partial).Length -ne $item.Bytes -or (Get-FileHash -LiteralPath $partial).Hash -ne $item.Sha256) { throw 'SILERO_DOWNLOAD_INTEGRITY_FAILED' }
    Move-Item -LiteralPath $partial -Destination $path -Force
    Write-Output ('VERIFIED ' + $item.Name + ' ' + $item.Bytes)
}
Write-Output ('TOTAL_BYTES ' + ($files | Measure-Object -Property Bytes -Sum).Sum)
