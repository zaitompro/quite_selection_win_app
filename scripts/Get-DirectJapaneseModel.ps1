[CmdletBinding()]
param([switch]$ConfirmDownload)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmDownload) { throw 'Explicit consent required: 288618087 bytes; see docs/direct-ja-ru-proposal.md.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$downloadRoot = Join-Path $projectRoot '.tools/translation-downloads'
New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
$name = 'opus-2021-02-18.zip'
$url = 'https://object.pouta.csc.fi/Tatoeba-MT-models/jpn-rus/' + $name
$destination = Join-Path $downloadRoot $name
$expectedBytes = 288618087
# Computed from the approved HTTPS download on 2026-10-19; not a publisher signature.
$expectedHash = 'ad601d0959cb408cf02e8c755c02b1dc0e67971ebac88c3b0c9645850617d0f3'
if (-not (Test-Path -LiteralPath $destination)) {
    $partial = $destination + '.partial'
    $offset = if (Test-Path -LiteralPath $partial) { (Get-Item -LiteralPath $partial).Length } else { 0L }
    if ($offset -gt $expectedBytes) { throw 'PARTIAL_DOWNLOAD_SIZE_INVALID' }
    Write-Output ('Downloading: ' + $name + ' from byte ' + $offset + ' of ' + $expectedBytes)
    if ($offset -lt $expectedBytes) {
        $client = [System.Net.Http.HttpClient]::new()
        $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $url)
        # ETag is used only to ensure resumed ranges belong to the same object; it is not a signature.
        $request.Headers.IfMatch.ParseAdd('"8f36bb92c2fb74691ea7b1dc1e1de9dc"')
        if ($offset -gt 0) { $request.Headers.Range = [System.Net.Http.Headers.RangeHeaderValue]::new($offset, $null) }
        $headersDeadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(3))
        try {
            $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead, $headersDeadline.Token).GetAwaiter().GetResult()
            $response.EnsureSuccessStatusCode() | Out-Null
            if ($offset -gt 0 -and ([int]$response.StatusCode -ne 206 -or $response.Content.Headers.ContentRange.From -ne $offset -or $response.Content.Headers.ContentRange.Length -ne $expectedBytes)) { throw 'RESUME_RANGE_MISMATCH' }
            $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $outputStream = [IO.FileStream]::new($partial, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            try {
                $outputStream.Position = $offset
                $buffer = [byte[]]::new(81920)
                $reported = $offset
                while ($true) {
                    $idle = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(3))
                    try { $read = $inputStream.ReadAsync($buffer, 0, $buffer.Length, $idle.Token).GetAwaiter().GetResult() }
                    finally { $idle.Dispose() }
                    if ($read -eq 0) { break }
                    $offset += $read
                    if ($offset -gt $expectedBytes) { throw 'DOWNLOAD_SIZE_EXCEEDED' }
                    $outputStream.Write($buffer, 0, $read)
                    if ($offset - $reported -ge 10000000) { Write-Output ('Received bytes: ' + $offset); $reported = $offset }
                }
                $outputStream.Flush($true)
            } finally { $outputStream.Dispose(); $inputStream.Dispose(); $response.Dispose() }
        } finally { $headersDeadline.Dispose(); $request.Dispose(); $client.Dispose() }
    }
    if ((Get-Item -LiteralPath ($destination + '.partial')).Length -ne $expectedBytes) { throw 'DOWNLOAD_SIZE_MISMATCH' }
    if ((Get-FileHash -LiteralPath ($destination + '.partial') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) { throw 'DOWNLOAD_HASH_MISMATCH' }
    Move-Item -LiteralPath ($destination + '.partial') -Destination $destination
}
if ((Get-Item -LiteralPath $destination).Length -ne $expectedBytes) { throw 'CACHED_DOWNLOAD_INVALID' }
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $expectedHash) { throw 'CACHED_DOWNLOAD_HASH_MISMATCH' }
$receiptPath = Join-Path $downloadRoot 'direct-ja-ru-receipt.json'
if (Test-Path -LiteralPath $receiptPath) {
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($receipt.sha256 -ne $hash) { throw 'CACHED_DOWNLOAD_HASH_CHANGED' }
}
[pscustomobject]@{name=$name; bytes=$expectedBytes; sha256=$hash; publisherHashVerified=$false; source=$url} |
    ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8
Write-Output ('DIRECT_MODEL_DOWNLOAD_COMPLETE sha256=' + $hash)
