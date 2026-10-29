[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$downloads = Join-Path $projectRoot '.tools/translation-downloads'
$runtime = Join-Path $projectRoot '.tools/translation-runtime'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$receipts = Get-Content -LiteralPath (Join-Path $downloads 'receipts.json') -Raw | ConvertFrom-Json
foreach ($item in $receipts | Where-Object name -NotLike '*.argosmodel') {
    $archive = Join-Path $downloads $item.name
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $item.sha256) { throw 'RUNTIME_HASH_MISMATCH' }
    $destination = if ($item.name.EndsWith('.whl')) { Join-Path $runtime 'packages' } else { $runtime }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $prefix = [IO.Path]::GetFullPath($destination).TrimEnd('\') + '\'
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        if ($zip.Entries.Count -gt 10000 -or ($zip.Entries | Measure-Object Length -Sum).Sum -gt 250MB) { throw 'ARCHIVE_LIMIT' }
        foreach ($entry in $zip.Entries) {
            $path = [IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
            if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $entry.FullName.Contains(':')) { throw 'ARCHIVE_PATH' }
            if ($entry.FullName.EndsWith('/')) { New-Item -ItemType Directory -Path $path -Force | Out-Null; continue }
            New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $true)
        }
    } finally { $zip.Dispose() }
}
# The embedded interpreter ignores global PATH, registry, user packages, and site hooks.
@('python313.zip','.','packages') | Set-Content -LiteralPath (Join-Path $runtime 'python313._pth') -Encoding ascii
Copy-Item -LiteralPath (Join-Path $downloads 'receipts.json') -Destination (Join-Path $runtime 'download-receipts.json')
# GPU code is not used by this CPU-only application. Keep the downloaded wheel intact.
$unusedCuda = Join-Path $runtime 'packages/ctranslate2/cudnn64_9.dll'
if (Test-Path -LiteralPath $unusedCuda) { Remove-Item -LiteralPath $unusedCuda }
& (Join-Path $runtime 'python.exe') -I -B -c 'import ctranslate2, sentencepiece, numpy, yaml; print("RUNTIME_IMPORT_OK", ctranslate2.__version__, sentencepiece.__version__)'
if ($LASTEXITCODE -ne 0) { throw 'RUNTIME_IMPORT_FAILED' }
Write-Output ('Runtime bytes: ' + (Get-ChildItem -LiteralPath $runtime -File -Recurse | Measure-Object Length -Sum).Sum)
