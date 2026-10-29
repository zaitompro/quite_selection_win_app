[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$downloads = Join-Path $projectRoot '.tools/piper-downloads'
$runtime = Join-Path $projectRoot '.tools/piper-runtime'
$items = Get-Content -LiteralPath (Join-Path $projectRoot 'third-party/piper/runtime.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($item in $items) {
    $archive = Join-Path $downloads $item.Name
    if ((Get-FileHash -LiteralPath $archive).Hash -ne $item.Sha256) { throw 'PIPER_RUNTIME_HASH_MISMATCH' }
    $prefix = [IO.Path]::GetFullPath($runtime).TrimEnd('\') + '\'
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        if ($zip.Entries.Count -gt 20000 -or ($zip.Entries | Measure-Object Length -Sum).Sum -gt 500MB) { throw 'PIPER_ARCHIVE_LIMIT' }
        foreach ($entry in $zip.Entries) {
            $path = [IO.Path]::GetFullPath((Join-Path $runtime $entry.FullName))
            if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $entry.FullName.Contains(':')) { throw 'PIPER_ARCHIVE_PATH' }
            if ($entry.FullName.EndsWith('/')) { New-Item -ItemType Directory -Path $path -Force | Out-Null; continue }
            New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $true)
        }
    } finally { $zip.Dispose() }
}
Write-Output ('RUNTIME_BYTES ' + (Get-ChildItem -LiteralPath $runtime -File -Recurse | Measure-Object Length -Sum).Sum)
