[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$')]
    [string] $Version = '0.3.0',
    [string] $DotNet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $repo 'artifacts/releases'
$name = "Storloom-$Version-win-x64"
$run = Join-Path $releaseRoot ($name + '-' + [guid]::NewGuid().ToString('N'))
$package = Join-Path $run $name
New-Item -ItemType Directory -Path $package -Force | Out-Null
$nugetRoot = (& $DotNet msbuild (Join-Path $repo 'src/Storloom.App/Storloom.App.csproj') -getProperty:NuGetPackageRoot -nologo).Trim()
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $nugetRoot -PathType Container)) {
    throw 'Cannot resolve the restored runtime license location.'
}
foreach ($entry in @(@('Storloom.App', $package), @('Storloom.Cli', (Join-Path $package 'cli')))) {
    & $DotNet publish (Join-Path $repo "src/$($entry[0])/$($entry[0]).csproj") -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false `
        "-p:Version=$Version" -o $entry[1] --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($entry[0])" }
    $runtimeConfig = Get-ChildItem -LiteralPath $entry[1] -Filter 'Storloom*.runtimeconfig.json' -File | Select-Object -First 1
    $frameworks = (Get-Content -LiteralPath $runtimeConfig.FullName -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks
    foreach ($framework in $frameworks) {
        $id = if ($framework.name -eq 'Microsoft.NETCore.App') { 'microsoft.netcore.app.runtime.win-x64' } else {
            'microsoft.windowsdesktop.app.runtime.win-x64'
        }
        $licenseRoot = Join-Path $nugetRoot "$id/$($framework.version)"
        $licenseTarget = Join-Path $entry[1] "licenses/$($framework.name)"
        New-Item -ItemType Directory -Path $licenseTarget -Force | Out-Null
        $notices = Get-ChildItem -LiteralPath $licenseRoot -File | Where-Object {
            $_.Name -match '^(LICENSE(?:\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$'
        }
        if (!$notices) { throw "Runtime license missing: $($framework.name)" }
        $notices | Copy-Item -Destination $licenseTarget
    }
}
# Explicit public inputs only. Never copy data/, source workspaces, or prior artifacts.
foreach ($file in @('README.md', 'README.zh-CN.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $package
}
foreach ($folder in @('rules', 'docs')) {
    $target = Join-Path $package $folder
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $files = if ($folder -eq 'rules') { @('builtin.json', 'browsers.json', 'development.json', 'desktop-apps.json',
        'communication-cn.json', 'media-cn.json', 'native-tools.json', 'sets.json', 'rules.schema.json') } else {
        @('rules.md', 'ai-rules.md', 'chat-exporters.md', 'quickstart.md', 'quickstart.zh-CN.md')
    }
    foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $repo "$folder/$file") -Destination $target -Force }
}
$revision = & git -C $repo rev-parse --verify HEAD 2>$null
@{ name = 'Storloom'; version = $Version; runtime = 'win-x64'; revision = $revision } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'build-info.json') -Encoding utf8
$forbidden = Get-ChildItem -LiteralPath $package -Recurse -Force | Where-Object {
    $_.Name -eq 'data' -or $_.Name -eq '.git' -or $_.Name -eq 'preferences.json' -or
    $_.Name -eq 'settings.json' -or $_.Extension -in @('.db', '.key', '.pfx', '.pem', '.pdb', '.log', '.jsonl', '.trx')
}
if ($forbidden) { throw 'Private state or debug files found in package; packaging stopped.' }
# Check textual inputs and this project's binaries for machine-specific build paths.
$identityMarkers = @($env:USERPROFILE, $repo, $env:COMPUTERNAME) | Where-Object { $_ -and $_.Length -gt 5 }
$productInfo = Get-Content -LiteralPath (Join-Path $repo 'src/Storloom.App/ProductInfo.cs') -Raw
$publicDeveloper = [regex]::Match($productInfo, 'public const string Developer = "([a-zA-Z0-9_-]+)";').Groups[1].Value
$publicRepository = [regex]::Match($productInfo, 'public const string Repository = "(https://github\.com/[a-zA-Z0-9_-]+/[a-zA-Z0-9_.-]+)";').Groups[1].Value
if (!$publicDeveloper -or !$publicRepository) { throw 'Public product attribution is missing.' }
$ownFiles = Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object {
    $_.Name -like 'Storloom*' -or $_.Extension -in @('.md', '.json')
}
foreach ($file in $ownFiles) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
        $text = $encoding.GetString($bytes)
        foreach ($marker in $identityMarkers) {
            $inspect = $text
            # The About page intentionally publishes these two complete metadata strings.
            # A matching computer name does not exempt any full profile/workspace path.
            if ($file.Name -eq 'Storloom.dll' -and $encoding.CodePage -eq 1200 -and
                $marker.Equals($publicDeveloper, [StringComparison]::OrdinalIgnoreCase)) {
                $inspect = $inspect.Replace(': ' + $publicDeveloper, '').Replace($publicRepository, '')
                # C# also stores const field values as length-prefixed UTF-16 metadata blobs.
                # Mask only complete blobs; never remove bare names from arbitrary paths/text.
                $metadataHex = [Convert]::ToHexString($bytes)
                foreach ($value in @($publicDeveloper, $publicRepository)) {
                    $payload = [Text.Encoding]::Unicode.GetBytes($value)
                    if ($payload.Length -ge 128) { throw 'Public attribution exceeds the supported metadata blob size.' }
                    $blob = $payload.Length.ToString('X2') + [Convert]::ToHexString($payload)
                    $metadataHex = $metadataHex.Replace($blob, ('00' * ($payload.Length + 1)))
                }
                $inspect = $encoding.GetString([Convert]::FromHexString($metadataHex)).Replace(': ' + $publicDeveloper, '').Replace($publicRepository, '')
            }
            if ($inspect.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "Machine-specific content found in $($file.Name); packaging stopped."
            }
        }
    }
}
$zip = Join-Path $run "$name.zip"
Compress-Archive -LiteralPath $package -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ascii
Write-Output $zip
Write-Output "SHA256 $hash"
