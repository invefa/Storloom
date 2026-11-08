[CmdletBinding()]
param([string] $Compiler = 'gcc')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$revision = '507be6bca8ce1fb977a061481f1d79e8c610e309'
$root = Join-Path $repo 'artifacts/toolchain/silk'
New-Item -ItemType Directory -Force -Path $root | Out-Null
$archive = Join-Path $root "$revision.zip"
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri "https://codeload.github.com/kn007/silk-v3-decoder/zip/$revision" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '5E4C662E9A2DC220156182972DB4E20ED1351885AA69DC3566F90ABC567CD4E9') { throw 'SILK SDK archive hash mismatch.' }
$build = Join-Path $root ('build-' + [guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $archive -DestinationPath $build
$source = Join-Path $build "silk-v3-decoder-$revision"
$sdk = Join-Path $source 'silk'
$binary = Join-Path $root 'storloom-silk.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $sdk 'src') -Filter '*.c' -File | ForEach-Object FullName)
& $Compiler '-O2' '-static' '-s' '-fno-ident' "-ffile-prefix-map=$source=." '-Wl,--no-insert-timestamp' '-I' (Join-Path $sdk 'interface') '-I' (Join-Path $sdk 'src') '-I' (Join-Path $sdk 'test') @sources (Join-Path $sdk 'test/Decoder.c') '-lm' '-o' $binary
if ($LASTEXITCODE -ne 0) { throw 'SILK SDK build failed; install a Windows x64 GCC toolchain.' }
$image = [IO.File]::ReadAllBytes($binary)
$pe = [BitConverter]::ToInt32($image, 0x3c)
if ($image.Length -lt $pe + 6 -or [BitConverter]::ToUInt16($image, $pe + 4) -ne 0x8664) { throw 'SILK helper must target Windows x64.' }
$licenses = Join-Path $root 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $licenses 'MIT.txt') -Force
$header = Get-Content -LiteralPath (Join-Path $sdk 'interface/SKP_Silk_SDK_API.h') -Raw
$header.Substring(0, $header.IndexOf('*/') + 2) | Set-Content -LiteralPath (Join-Path $licenses 'Skype-SDK.txt') -Encoding utf8
[pscustomobject]@{ Binary = $binary; Licenses = $licenses }
