$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$files = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs','.cpp','.xaml','.py') }
$patterns = @('Clipboard\s*\.', 'OpenClipboard\s*\(', 'GetClipboardData\s*\(', 'SetClipboardData\s*\(', 'DocumentRange', '\.Current\.(Name|Value)\b', 'HttpClient', 'WebClient', 'https?://', 'Console\.(Write|Error\.Write).*ex\.(Message|ToString|StackTrace)', 'SendKeys', 'Invoke-WebRequest')
$violations = @()
foreach ($file in $files) {
    foreach ($pattern in $patterns) {
        # The fixed-catalog downloader has no text/image parameter. No other source is exempt.
        if ($file.FullName.EndsWith('Translation\Downloads\ModelPreparation.cs') -and $pattern -in @('HttpClient','https?://')) { continue }
        if ($file.FullName.EndsWith('Speech\Downloads\RhVoiceInstallation.cs') -and $pattern -in @('HttpClient','https?://')) { continue }
        if ($file.FullName.EndsWith('Speech\Downloads\PiperModels.cs') -and $pattern -in @('HttpClient','https?://')) { continue }
        if ($file.FullName.EndsWith('Speech\Downloads\AdditionalModels.cs') -and $pattern -in @('HttpClient','https?://')) { continue }
        if ($file.FullName.EndsWith('Speech\Downloads\SileroModels.cs') -and $pattern -in @('HttpClient','https?://')) { continue }
        if (Select-String -LiteralPath $file.FullName -Pattern $pattern -Quiet) { $violations += [pscustomobject]@{File=$file.FullName.Substring($projectRoot.Length+1);Rule=$pattern} }
    }
}
if ($violations.Count -gt 0) { $violations | ConvertTo-Json; throw 'PRIVACY_STATIC_CHECK_FAILED' }
Write-Output 'PASS: prohibited direct data-transfer, whole-document fallback, network-client and raw-exception logging patterns absent from app sources.'
Write-Output 'Scope: source pattern scan only. Framework/native binaries are not proven leak-free by this test.'
