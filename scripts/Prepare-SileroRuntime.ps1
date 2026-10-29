[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$downloads = Join-Path $projectRoot '.tools/silero-downloads'
$runtime = Join-Path $projectRoot '.tools/silero-runtime'
$items = Get-Content -LiteralPath (Join-Path $projectRoot 'third-party/silero/runtime.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($item in $items) {
    $archive = Join-Path $downloads $item.Name
    if ((Get-Item -LiteralPath $archive).Length -ne $item.Bytes -or (Get-FileHash -LiteralPath $archive).Hash -ne $item.Sha256) { throw 'SILERO_RUNTIME_HASH_MISMATCH' }
    $prefix = [IO.Path]::GetFullPath($runtime).TrimEnd('\') + '\'
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        if ($zip.Entries.Count -gt 30000 -or ($zip.Entries | Measure-Object Length -Sum).Sum -gt 1GB) { throw 'SILERO_ARCHIVE_LIMIT' }
        foreach ($entry in $zip.Entries) {
            $path = [IO.Path]::GetFullPath((Join-Path $runtime $entry.FullName))
            if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $entry.FullName.Contains(':')) { throw 'SILERO_ARCHIVE_PATH' }
            if ($entry.FullName.EndsWith('/')) { New-Item -ItemType Directory -Path $path -Force | Out-Null; continue }
            New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $true)
        }
    } finally { $zip.Dispose() }
}
Write-Output ('RUNTIME_BYTES ' + (Get-ChildItem -LiteralPath $runtime -File -Recurse | Measure-Object Length -Sum).Sum)
