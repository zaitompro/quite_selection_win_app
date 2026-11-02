[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PackageArtifacts.ps1')
$projectRoot = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $projectRoot ('artifacts/package-staging/' + [Guid]::NewGuid().ToString('N'))
$packageDirectory = Join-Path $projectRoot 'artifacts/packages'
$buildDirectory = Join-Path $projectRoot 'src/QuietSelection.App/bin/Release/net10.0-windows'
$packagePath = $null
$packageComplete = $false
try {
    New-Item -ItemType Directory -Path $staging, $packageDirectory -Force | Out-Null
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_DISABLE_GUI_ERRORS = '1'
    # AppHostRelativeDotNet is applied by Publish, not Build.
    & (Join-Path $projectRoot '.tools/dotnet/dotnet.exe') publish (Join-Path $projectRoot 'src/QuietSelection.App/QuietSelection.App.csproj') -c Release --no-build --no-restore -o $staging --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'PACKAGE_PUBLISH_FAILED' }
    foreach ($name in @('QuietSelection.Native.dll')) {
        Copy-Item -LiteralPath (Join-Path $buildDirectory $name) -Destination $staging
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging/AppxManifest.xml') -Destination $staging
    # The magnifier runs as a copy of the launcher whose manifest asks for UIAccess
    # (MagSetInputTransform needs it). Only this process gets the privilege.
    $magnifierExe = Join-Path $staging 'QuietSelection.Magnifier.exe'
    Copy-Item -LiteralPath (Join-Path $staging 'QuietSelection.exe') -Destination $magnifierExe
    $uiAccessManifest = Join-Path $staging 'uiaccess.manifest'
    $manifestText = Get-Content -LiteralPath (Join-Path $projectRoot 'src/QuietSelection.App/app.manifest') -Raw
    if ($manifestText -notmatch 'uiAccess="false"') { throw 'APP_MANIFEST_UIACCESS_MARKER_MISSING' }
    [IO.File]::WriteAllText($uiAccessManifest, ($manifestText -replace 'uiAccess="false"', 'uiAccess="true"'))
    $mt = 'C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/mt.exe'
    & $mt -nologo -manifest $uiAccessManifest "-outputresource:$magnifierExe;#1"
    if ($LASTEXITCODE -ne 0) { throw 'MAGNIFIER_MANIFEST_FAILED' }
    Remove-Item -LiteralPath $uiAccessManifest
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $staging
    $runtime = Join-Path $staging 'runtime'
    New-Item -ItemType Directory -Path (Join-Path $runtime 'host'), (Join-Path $runtime 'shared') -Force | Out-Null
    $sdk = Join-Path $projectRoot '.tools/dotnet'
    Copy-Item -LiteralPath (Join-Path $sdk 'host/fxr') -Destination (Join-Path $runtime 'host') -Recurse
    foreach ($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
        Copy-Item -LiteralPath (Join-Path $sdk "shared/$framework") -Destination (Join-Path $runtime 'shared') -Recurse
    }
    foreach ($name in @('LICENSE.txt','ThirdPartyNotices.txt')) {
        if (Test-Path -LiteralPath (Join-Path $sdk $name)) { Copy-Item -LiteralPath (Join-Path $sdk $name) -Destination $runtime }
    }
    # Logos are generated from the app mark: QuietSelection.exe --export-icons assets/icons
    $assetDirectory = Join-Path $staging 'Assets'
    New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
    foreach ($asset in @('StoreLogo.png','Square44x44Logo.png','Square150x150Logo.png')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot "assets/icons/$asset") -Destination $assetDirectory
    }
    $makeappx = 'C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/makeappx.exe'
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'packaging/AppxManifest.xml') -Raw
    $packageVersion = $manifest.Package.Identity.Version
    $packagePath = Join-Path $packageDirectory ('QuietSelection_' + $packageVersion + '_x64_unsigned.msix')
    & $makeappx pack /d $staging /p $packagePath /o /v *> (Join-Path $packageDirectory 'makeappx.log')
    if ($LASTEXITCODE -ne 0) { throw 'MSIX_PACK_FAILED' }
    $start = [System.Diagnostics.ProcessStartInfo]::new((Join-Path $staging 'QuietSelection.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.Arguments = '--runtime-check' # ArgumentList is missing in Windows PowerShell 5.1.
    # Prove that the package is independent of both the local SDK and registered runtimes.
    [void]$start.Environment.Remove('DOTNET_ROOT')
    [void]$start.Environment.Remove('DOTNET_ROOT_X64')
    $runtimeCheck = [System.Diagnostics.Process]::Start($start)
    try {
        if (-not $runtimeCheck.WaitForExit(30000)) {
            # This is the exact child just started from this staging directory, never a name-based kill.
            $runtimeCheck.Kill()
            $runtimeCheck.WaitForExit()
            throw 'PRIVATE_RUNTIME_STARTUP_CHECK_TIMEOUT'
        }
        if ($runtimeCheck.ExitCode -ne 0) { throw 'PRIVATE_RUNTIME_STARTUP_CHECK_FAILED' }
    } finally { $runtimeCheck.Dispose() }
    [pscustomobject]@{Package=$packagePath;Bytes=(Get-Item -LiteralPath $packagePath).Length;PrivateRuntimeCheck='Passed'} | ConvertTo-Json -Compress
    $packageComplete = $true
    Remove-ObsoletePackages -ProjectRoot $projectRoot -KeepFileName (Split-Path -Leaf $packagePath) -UnsignedOnly
    Write-Output 'Unsigned package built. No certificate was created, trusted, or installed.'
} finally {
    try {
        if (-not $packageComplete -and $packagePath -and (Test-Path -LiteralPath $packagePath)) {
            Assert-PackageArtifactPath -ProjectRoot $projectRoot -Path $packagePath
            Remove-Item -LiteralPath $packagePath -Force -ErrorAction Stop
        }
    } finally {
        Remove-PackageStaging -ProjectRoot $projectRoot -Directory $staging
    }
}
