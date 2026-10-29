# Shared by packaging and installation. Delete only known outputs inside this project.
function Assert-PackageArtifactPath {
    param([string]$ProjectRoot, [string]$Path)
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $target = [IO.Path]::GetFullPath($Path)
    if (-not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'PACKAGE_CLEANUP_OUTSIDE_PROJECT'
    }
    # Reject junctions/symlinks, including in the project path itself.
    $cursor = $target
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'PACKAGE_CLEANUP_REPARSE_POINT' }
        }
        $cursor = Split-Path -Parent $cursor
    }
}

function Remove-PackageStaging {
    param([Parameter(Mandatory)][string]$ProjectRoot, [Parameter(Mandatory)][string]$Directory)
    $parent = [IO.Path]::GetFullPath((Join-Path $ProjectRoot 'artifacts/package-staging'))
    $target = [IO.Path]::GetFullPath($Directory)
    if ((Split-Path -Parent $target) -ne $parent -or (Split-Path -Leaf $target) -notmatch '^[a-f0-9]{32}$') {
        throw 'PACKAGE_CLEANUP_INVALID_STAGING_DIRECTORY'
    }
    Assert-PackageArtifactPath -ProjectRoot $ProjectRoot -Path $target
    if (-not (Test-Path -LiteralPath $target)) { return }
    # Check the entire tree before recursive deletion; never follow a linked directory.
    $linked = Get-ChildItem -LiteralPath $target -Force -Recurse -ErrorAction Stop |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1
    if ($linked) { throw 'PACKAGE_CLEANUP_REPARSE_POINT' }
    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
}

function Remove-ObsoletePackages {
    param(
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)][string]$KeepFileName,
        [switch]$UnsignedOnly
    )
    $pattern = '^QuietSelection_\d+\.\d+\.\d+\.\d+_x64(_unsigned)?\.msix$'
    if ($KeepFileName -notmatch $pattern) { throw 'PACKAGE_CLEANUP_INVALID_PACKAGE_NAME' }
    $directory = Join-Path $ProjectRoot 'artifacts/packages'
    $keep = Join-Path $directory $KeepFileName
    Assert-PackageArtifactPath -ProjectRoot $ProjectRoot -Path $keep
    if (-not (Test-Path -LiteralPath $keep -PathType Leaf)) { throw 'PACKAGE_CLEANUP_KEEP_FILE_MISSING' }
    $candidates = @(Get-ChildItem -LiteralPath $directory -File -Force | Where-Object {
        $_.Name -match $pattern -and $_.Name -ne $KeepFileName -and
        (-not $UnsignedOnly -or $_.Name.EndsWith('_unsigned.msix'))
    })
    # Complete all path checks before removing any package.
    foreach ($file in $candidates) { Assert-PackageArtifactPath -ProjectRoot $ProjectRoot -Path $file.FullName }
    foreach ($file in $candidates) { Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop }
}
