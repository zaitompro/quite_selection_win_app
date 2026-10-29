[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$downloads = Join-Path $projectRoot '.tools/piper-downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
$runtime = Get-Content -LiteralPath (Join-Path $projectRoot 'third-party/piper/runtime.json') -Raw | ConvertFrom-Json
$models = Get-Content -LiteralPath (Join-Path $projectRoot 'src/QuietSelection.App/Speech/Downloads/piper-models.json') -Raw | ConvertFrom-Json
$files = @($runtime) + @($models.Files)
foreach ($item in $files) {
    $path = Join-Path $downloads $item.Name
    if ((Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).Length -eq $item.Bytes -and (Get-FileHash -LiteralPath $path).Hash -eq $item.Sha256) {
        Write-Output ('REUSED ' + $item.Name); continue
    }
    $partial = $path + '.partial'
    $output = [IO.File]::Open($partial, [IO.FileMode]::Create, [IO.FileAccess]::Write)
    try {
        for ([long]$offset=0; $offset -lt $item.Bytes; $offset += 4MB) {
            $end = [Math]::Min($item.Bytes - 1, $offset + 4MB - 1)
            $rangeFile = $partial + '.range'
            & curl.exe --http1.1 --fail --location --silent --show-error --proto '=https' --proto-redir '=https' --connect-timeout 30 --max-time 120 --retry 2 --range ($offset.ToString()+'-'+$end.ToString()) --output $rangeFile $item.Url
            if ($LASTEXITCODE -ne 0 -or (Get-Item -LiteralPath $rangeFile).Length -ne ($end-$offset+1)) { throw 'PIPER_DOWNLOAD_RANGE_FAILED' }
            $input = [IO.File]::OpenRead($rangeFile)
            try { $input.CopyTo($output) } finally { $input.Dispose() }
            $output.Flush()
            Write-Output ('RECEIVED ' + $item.Name + ' ' + ($end+1) + '/' + $item.Bytes)
        }
    } finally { $output.Dispose() }
    if ((Get-Item -LiteralPath $partial).Length -ne $item.Bytes -or (Get-FileHash -LiteralPath $partial).Hash -ne $item.Sha256) { throw 'PIPER_DOWNLOAD_INTEGRITY_FAILED' }
    Move-Item -LiteralPath $partial -Destination $path -Force
    Write-Output ('VERIFIED ' + $item.Name + ' ' + $item.Bytes)
}
Write-Output ('TOTAL_BYTES ' + ($files | Measure-Object -Property Bytes -Sum).Sum)
