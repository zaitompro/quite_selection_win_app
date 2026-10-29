[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sdkVersion = '10.0.402'
$sdkDirectory = Join-Path $projectRoot '.tools/dotnet'
$downloadDirectory = Join-Path $projectRoot '.tools/downloads'
$sdkExecutable = Join-Path $sdkDirectory 'dotnet.exe'
if (Test-Path -LiteralPath $sdkExecutable) {
    $installed = & $sdkExecutable --version
    if ($LASTEXITCODE -eq 0 -and $installed -eq $sdkVersion) {
        Write-Output "SDK $sdkVersion already available in project."
        exit 0
    }
    throw 'SDK_DIRECTORY_ALREADY_OCCUPIED'
}
$metadataUri = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$archiveUri = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip"
$metadata = Invoke-RestMethod -Uri $metadataUri
$candidates = @($metadata.releases | ForEach-Object { $_.sdks } | Where-Object version -eq $sdkVersion)
$sdkFile = @($candidates | ForEach-Object { $_.files } | Where-Object { $_.rid -eq 'win-x64' -and $_.name.EndsWith('.zip') }) | Select-Object -First 1
if (-not $sdkFile -or $sdkFile.hash -notmatch '^[a-fA-F0-9]{128}$' -or $sdkFile.url -ne $archiveUri) {
    throw 'OFFICIAL_SDK_METADATA_MISMATCH'
}
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
$archivePath = Join-Path $downloadDirectory "dotnet-sdk-$sdkVersion-win-x64.zip"
Write-Output "Downloading Microsoft .NET SDK $sdkVersion (Windows x64)."
Invoke-WebRequest -Uri $archiveUri -OutFile $archivePath
$actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash
if ($actualHash -ne $sdkFile.hash) { throw 'SDK_SHA512_MISMATCH' }
Write-Output "SHA512 verified. Archive bytes: $((Get-Item -LiteralPath $archivePath).Length)"
New-Item -ItemType Directory -Path $sdkDirectory -Force | Out-Null
Expand-Archive -LiteralPath $archivePath -DestinationPath $sdkDirectory
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& $sdkExecutable --info
if ($LASTEXITCODE -ne 0) { throw 'SDK_VERIFICATION_FAILED' }
Write-Output 'Project-local SDK ready. System PATH has not been changed.'
