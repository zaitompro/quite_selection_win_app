$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $projectRoot 'scripts/PackageArtifacts.ps1')
$fixture = Join-Path $projectRoot ('artifacts/test-results/package-cleanup-' + [Guid]::NewGuid().ToString('N'))
$packages = Join-Path $fixture 'artifacts/packages'
$staging = Join-Path $fixture ('artifacts/package-staging/' + [Guid]::NewGuid().ToString('N'))
$passed = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
}
function Assert-Rejected([scriptblock]$Action, [string]$Code) {
    $errorMessage = $null
    try { & $Action } catch { $errorMessage = $_.Exception.Message }
    Assert-True ($errorMessage -eq $Code) "Expected $Code, received $errorMessage"
}
New-Item -ItemType Directory -Path $packages, $staging -Force | Out-Null
try {
    foreach ($name in @('QuietSelection_0.1.0.20_x64.msix', 'QuietSelection_0.1.0.20_x64_unsigned.msix',
        'QuietSelection_0.1.0.21_x64.msix', 'QuietSelection_0.1.0.21_x64_unsigned.msix',
        'another-app.msix', 'installation.json')) {
        Set-Content -LiteralPath (Join-Path $packages $name) -Value 'fixture'
    }
    Remove-ObsoletePackages -ProjectRoot $fixture -KeepFileName 'QuietSelection_0.1.0.21_x64_unsigned.msix' -UnsignedOnly
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $packages 'QuietSelection_0.1.0.20_x64_unsigned.msix'))) 'Old unsigned retained'
    Assert-True (Test-Path -LiteralPath (Join-Path $packages 'QuietSelection_0.1.0.20_x64.msix')) 'Previous signed package lost before install'
    Assert-Rejected { Remove-ObsoletePackages -ProjectRoot $fixture -KeepFileName 'QuietSelection_0.1.0.99_x64.msix' } 'PACKAGE_CLEANUP_KEEP_FILE_MISSING'
    Assert-Rejected { Remove-ObsoletePackages -ProjectRoot $fixture -KeepFileName '../QuietSelection_0.1.0.21_x64.msix' } 'PACKAGE_CLEANUP_INVALID_PACKAGE_NAME'
    Assert-True ((Get-ChildItem -LiteralPath $packages -File).Count -eq 5) 'Rejected operation deleted files'
    Remove-ObsoletePackages -ProjectRoot $fixture -KeepFileName 'QuietSelection_0.1.0.21_x64.msix'
    Assert-True ((Get-ChildItem -LiteralPath $packages -File).Count -eq 3) 'Unexpected retention count'
    Assert-True (Test-Path -LiteralPath (Join-Path $packages 'QuietSelection_0.1.0.21_x64.msix')) 'Latest signed removed'
    Assert-True (Test-Path -LiteralPath (Join-Path $packages 'another-app.msix')) 'Unrelated file removed'
    Assert-True (Test-Path -LiteralPath (Join-Path $packages 'installation.json')) 'Receipt removed'
    Assert-Rejected { Remove-PackageStaging -ProjectRoot $fixture -Directory $packages } 'PACKAGE_CLEANUP_INVALID_STAGING_DIRECTORY'
    Assert-Rejected { Assert-PackageArtifactPath -ProjectRoot $fixture -Path (Join-Path $projectRoot 'outside') } 'PACKAGE_CLEANUP_OUTSIDE_PROJECT'
    $protected = Join-Path $fixture 'protected'
    New-Item -ItemType Directory -Path $protected | Out-Null
    Set-Content -LiteralPath (Join-Path $protected 'keep.txt') -Value 'keep'
    $junction = Join-Path $staging 'linked'
    New-Item -ItemType Junction -Path $junction -Target $protected | Out-Null
    Assert-Rejected { Remove-PackageStaging -ProjectRoot $fixture -Directory $staging } 'PACKAGE_CLEANUP_REPARSE_POINT'
    Assert-True (Test-Path -LiteralPath (Join-Path $protected 'keep.txt')) 'Junction target damaged'
    Remove-Item -LiteralPath $junction -Force
    Set-Content -LiteralPath (Join-Path $staging 'temporary.txt') -Value 'temporary'
    Remove-PackageStaging -ProjectRoot $fixture -Directory $staging
    Assert-True (-not (Test-Path -LiteralPath $staging)) 'Staging retained'
    Remove-PackageStaging -ProjectRoot $fixture -Directory $staging
    # Exercise the actual wrappers with a missing SDK: packaging must still clean
    # its newly created directory, and failed installation must preserve packages.
    $fixtureScripts = Join-Path $fixture 'scripts'
    New-Item -ItemType Directory -Path $fixtureScripts | Out-Null
    foreach ($scriptName in @('PackageArtifacts.ps1', 'Build-Package.ps1', 'Install-LocalPackage.ps1')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot "scripts/$scriptName") -Destination $fixtureScripts
    }
    $shell = (Get-Process -Id $PID).Path
    & $shell -NoProfile -File (Join-Path $fixtureScripts 'Build-Package.ps1') *> $null
    Assert-True ($LASTEXITCODE -ne 0) 'Missing SDK should fail packaging'
    Assert-True (@(Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts/package-staging') -Directory).Count -eq 0) 'Failed build left staging behind'
    & $shell -NoProfile -File (Join-Path $fixtureScripts 'Install-LocalPackage.ps1') -ConfirmLocalPublisherTrust *> $null
    Assert-True ($LASTEXITCODE -ne 0) 'Missing SDK should fail installation'
    Assert-True (Test-Path -LiteralPath (Join-Path $packages 'QuietSelection_0.1.0.21_x64.msix')) 'Failed installation removed signed package'
    Write-Output "$passed package cleanup checks passed."
} finally {
    Assert-PackageArtifactPath -ProjectRoot $projectRoot -Path $fixture
    if ((Split-Path -Parent $fixture) -ne (Join-Path $projectRoot 'artifacts/test-results')) { throw 'INVALID_FIXTURE_ROOT' }
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
