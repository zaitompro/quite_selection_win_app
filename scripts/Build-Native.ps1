[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$vswhere = 'C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe'
$compiler = @(& $vswhere -latest -products '*' -find 'VC\Tools\MSVC\*\bin\Hostx64\x64\cl.exe') | Select-Object -Last 1
if (-not $compiler) { throw 'MSVC_X64_COMPILER_REQUIRED' }
$msvcRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $compiler)))
$sdkRoot = 'C:/Program Files (x86)/Windows Kits/10'
$sdkVersion = '10.0.26100.0'
$outputDirectory = Join-Path $projectRoot "artifacts/native/$Configuration"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$arguments = @('/nologo','/std:c++20','/EHsc','/W4','/WX','/O2','/MT','/LD','/Zc:__cplusplus','/DWIN32_LEAN_AND_MEAN','/DNOMINMAX','/DUNICODE','/D_UNICODE')
$arguments += '/I' + (Join-Path $msvcRoot 'include')
foreach ($part in @('ucrt','shared','um','winrt','cppwinrt')) { $arguments += '/I' + (Join-Path $sdkRoot "Include/$sdkVersion/$part") }
$arguments += (Join-Path $projectRoot 'src/QuietSelection.Native/NativeBridge.cpp')
$arguments += (Join-Path $projectRoot 'src/QuietSelection.Native/SapiSpeech.cpp')
$arguments += '/Fo' + $outputDirectory + '/'
$arguments += '/Fe' + (Join-Path $outputDirectory 'QuietSelection.Native.dll')
$arguments += @('/link','/MACHINE:X64','/DYNAMICBASE','/NXCOMPAT','windowsapp.lib','ole32.lib','sapi.lib')
$arguments += '/LIBPATH:' + (Join-Path $msvcRoot 'lib/x64')
$arguments += '/LIBPATH:' + (Join-Path $sdkRoot "Lib/$sdkVersion/ucrt/x64")
$arguments += '/LIBPATH:' + (Join-Path $sdkRoot "Lib/$sdkVersion/um/x64")
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'NATIVE_BUILD_FAILED' }
