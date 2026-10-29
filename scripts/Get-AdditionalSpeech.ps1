[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$items = @(
    @{Folder='kokoro-downloads';Name='kokoro-v1.0.int8.onnx';Sha256='6e742170d309016e5891a994e1ce1559c702a2ccd0075e67ef7157974f6406cb';Bytes=92361271;Url='https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/kokoro-v1.0.int8.onnx'},
    @{Folder='kokoro-downloads';Name='voices-v1.0.bin';Sha256='bca610b8308e8d99f32e6fe4197e7ec01679264efed0cac9140fe9c29f1fbf7d';Bytes=28214398;Url='https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/voices-v1.0.bin'},
    @{Folder='voicevox-downloads';Name='voicevox_core-windows-x64-0.17.0.zip';Bytes=1447774;Sha256='5a116b73fe338d36e63ae651279246f5968d0866a73780209c5f7b43c9692216';Url='https://github.com/VOICEVOX/voicevox_core/releases/download/0.17.0/voicevox_core-windows-x64-0.17.0.zip'},
    @{Folder='voicevox-downloads';Name='voicevox_onnxruntime-win-x64-1.17.3.tgz';Sha256='32b07e7c6c59030434b54eb0255912510942faf193c7a14e0e1a168db5796265';Bytes=4650448;Url='https://github.com/VOICEVOX/onnxruntime-builder/releases/download/voicevox_onnxruntime-1.17.3/voicevox_onnxruntime-win-x64-1.17.3.tgz'},
    @{Folder='voicevox-downloads';Name='open_jtalk_dic_utf_8-1.11.tar.gz';Sha256='fe6ba0e43542cef98339abdffd903e062008ea170b04e7e2a35da805902f382a';Bytes=23646843;Url='https://github.com/r9y9/open_jtalk/releases/download/v1.11.1/open_jtalk_dic_utf_8-1.11.tar.gz'}
)
$report = @()
foreach($item in $items) {
    $dir = Join-Path $root ('.tools/' + $item.Folder)
    New-Item -ItemType Directory -Force $dir | Out-Null
    $path = Join-Path $dir $item.Name
    if (!(Test-Path -LiteralPath $path)) {
        Write-Output ('DOWNLOADING ' + $item.Name)
        & curl.exe --ipv4 --http1.1 --location --fail --silent --show-error --proto '=https' --proto-redir '=https' --connect-timeout 30 --max-time 1800 --speed-limit 1024 --speed-time 45 --retry 2 --retry-delay 2 --max-filesize $item.Bytes --output ($path + '.partial') $item.Url
        if($LASTEXITCODE -ne 0) { throw ('DOWNLOAD_FAILED ' + $item.Name) }
        Move-Item -LiteralPath ($path + '.partial') -Destination $path
    }
    if ((Get-Item -LiteralPath $path).Length -ne $item.Bytes) { throw 'DOWNLOAD_SIZE_MISMATCH' }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($item.Sha256 -and $hash -ne $item.Sha256) { throw 'DOWNLOAD_HASH_MISMATCH' }
    $report += [ordered]@{Name=$item.Name;Bytes=$item.Bytes;Sha256=$hash;Url=$item.Url;PublisherDigest=($item.Name -eq 'voicevox_core-windows-x64-0.17.0.zip')}
    $report | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $root 'artifacts/test-results/additional-speech-downloads.json')
    Write-Output ('COMPLETE ' + $item.Name + ' ' + $hash)
}
