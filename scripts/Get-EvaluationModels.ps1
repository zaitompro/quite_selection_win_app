[CmdletBinding()]
param([switch]$ConfirmDownload)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmDownload) { throw 'DOWNLOAD_CONSENT_REQUIRED' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'catalogs/tts-evaluation.json') -Raw | ConvertFrom-Json
$report = [Collections.Generic.List[object]]::new()
function Verify-File($Path, $Entry) {
    if (-not (Test-Path -LiteralPath $Path) -or (Get-Item -LiteralPath $Path).Length -ne $Entry.Bytes) { throw 'MODEL_SIZE_MISMATCH' }
    $sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Entry.Sha256) {
        if ($sha256 -ne $Entry.Sha256) { throw 'MODEL_SHA256_MISMATCH' }
    } elseif ($Entry.GitBlob) {
        # Small non-LFS files are authenticated by the pinned repository's Git blob.
        if ($Entry.Bytes -gt 1MB) { throw 'NON_LFS_FILE_TOO_LARGE' }
        $bytes = [IO.File]::ReadAllBytes($Path)
        $header = [Text.Encoding]::UTF8.GetBytes("blob $($bytes.Length)`0")
        $hash = [Security.Cryptography.SHA1]::Create()
        try { $actual = [Convert]::ToHexString($hash.ComputeHash([byte[]]($header + $bytes))).ToLowerInvariant() }
        finally { $hash.Dispose() }
        if ($actual -ne $Entry.GitBlob) { throw 'MODEL_GIT_BLOB_MISMATCH' }
    } else { throw 'MODEL_EXPECTED_HASH_MISSING' }
    return $sha256
}
foreach ($model in $catalog.Models) {
    $cache = [IO.Path]::GetFullPath((Join-Path $projectRoot ('.tools/' + $model.CacheDirectory)))
    $toolsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '.tools')) + [IO.Path]::DirectorySeparatorChar
    if (-not $cache.StartsWith($toolsRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'CACHE_PATH_REFUSED' }
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    foreach ($entry in $model.Files) {
        $path = [IO.Path]::GetFullPath((Join-Path $cache $entry.Name))
        if (-not $path.StartsWith($cache + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'MODEL_PATH_REFUSED' }
        if (-not (Test-Path -LiteralPath $path)) {
            $uri = [Uri]$entry.Url
            if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('huggingface.co','raw.githubusercontent.com')) { throw 'MODEL_URL_REFUSED' }
            New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
            Write-Output ("DOWNLOADING " + $model.Name + ': ' + $entry.Name)
            # Each transfer and integrity check finishes before the next file starts.
            & curl.exe --ipv4 --http1.1 --location --fail --silent --show-error --proto '=https' --proto-redir '=https' --connect-timeout 30 --max-time 1800 --speed-limit 1024 --speed-time 45 --retry 2 --retry-delay 2 --max-filesize $entry.Bytes --output ($path + '.partial') $entry.Url
            if ($LASTEXITCODE -ne 0) { throw 'MODEL_DOWNLOAD_FAILED' }
            $null = Verify-File ($path + '.partial') $entry
            Move-Item -LiteralPath ($path + '.partial') -Destination $path
        }
        $sha256 = Verify-File $path $entry
        $report.Add([ordered]@{ Model=$model.Name; Name=$entry.Name; Bytes=$entry.Bytes; Sha256=$sha256; Url=$entry.Url })
        Write-Output ('VERIFIED ' + $model.Name + ': ' + $entry.Name)
    }
    Write-Output ('COMPLETE ' + $model.Name)
}
$reportDirectory = Join-Path $projectRoot 'artifacts/test-results'
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reportDirectory 'tts-evaluation-downloads.json') -Encoding utf8
