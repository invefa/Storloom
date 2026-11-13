[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string] $KeepVersion,
    [string] $Output
)
$ErrorActionPreference = 'Stop'
$taskRepo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (!$KeepVersion) { $KeepVersion = ([xml](Get-Content -LiteralPath (Join-Path $taskRepo 'Directory.Build.props') -Raw)).Project.PropertyGroup[0].Version }
if (!$Output) { $Output = Join-Path $taskRepo 'artifacts/operations/storloom-build-cleanup.rules.json' }
$taskRules = [Collections.Generic.List[object]]::new()
$taskExclusions = @('**/data', '**/data/**', '**/.local/**', '**/backups/**', '**/archives/**', '**/keys/**', '**/*.key', '**/*.pem', '**/*.pfx')
function Add-LocalRule($Id, $Name, $Zh, $Description, [string[]]$Roots, [string[]]$Includes = @('**'), [double]$Age = 0, [bool]$Recursive = $true, [string[]]$Processes = @('Storloom','Storloom.Cli')) {
    if (!$Roots.Count) { return }
    foreach ($taskRoot in $Roots) {
        $taskAbsolute = [IO.Path]::GetFullPath($taskRoot)
        if (!$taskAbsolute.StartsWith($taskRepo + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup root is outside the repository.' }
        if ((Get-Item -LiteralPath $taskAbsolute).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Cleanup roots cannot be links.' }
    }
    $taskRules.Add([ordered]@{id=$Id;name=$Name;names=@{'zh-CN'=$Zh};description=$Description;roots=@($Roots);include=@($Includes);exclude=$taskExclusions;processes=@($Processes);risk='Review';minimumAgeHours=$Age;recursive=$Recursive})
}
$taskLocal = Join-Path $taskRepo 'artifacts/local'
$taskOldPrograms = @(Get-ChildItem -LiteralPath $taskLocal -Directory | Where-Object {
    $_.Name -match '^Storloom-(\d+\.\d+\.\d+)(?:-[a-f0-9]+)?$' -and [version]$Matches[1] -lt [version]$KeepVersion
} | Select-Object -ExpandProperty FullName)
Add-LocalRule 'local-storloom-old-programs' 'Storloom superseded program builds' 'Storloom 旧程序构建' "Explicit program directories older than $KeepVersion. Shared data and key directories are excluded. Review every candidate before deletion." $taskOldPrograms
$taskReleaseRoot = Join-Path $taskRepo 'artifacts/releases'
$taskOldPackages = @(Get-ChildItem -LiteralPath $taskReleaseRoot -Directory | Where-Object {
    $_.Name -match '^Storloom-(\d+\.\d+\.\d+)-win-x64-[a-f0-9]+$' -and [version]$Matches[1] -lt [version]$KeepVersion
} | Select-Object -ExpandProperty FullName)
Add-LocalRule 'local-storloom-old-packages' 'Storloom superseded release packages' 'Storloom 旧发布包' "Explicit release directories older than $KeepVersion. Current and future versions are not matched." $taskOldPackages
$taskCache = @(foreach ($taskArea in @('src','tests')) {
    foreach ($taskProject in Get-ChildItem -LiteralPath (Join-Path $taskRepo $taskArea) -Directory) {
        if (!(Get-ChildItem -LiteralPath $taskProject.FullName -Filter '*.csproj' -File)) { continue }
        foreach ($taskKind in @('bin','obj')) { $taskPath = Join-Path $taskProject.FullName $taskKind; if (Test-Path -LiteralPath $taskPath) { $taskPath } }
    }
})
Add-LocalRule 'local-storloom-build-cache' 'Storloom regenerable build files' 'Storloom 可重建的 bin 与 obj 缓存' 'Project build files older than 24 hours. Does not include toolchains, global package caches, task checkpoints or installed program data.' $taskCache @('**') 24 $true @('dotnet','MSBuild','testhost','Storloom','Storloom.Cli')
$taskShortcuts = @(Get-ChildItem -LiteralPath $taskLocal -Filter '*.lnk' -File | Where-Object {
    $_.BaseName -match '^Storloom-(\d+\.\d+\.\d+)$' -and [version]$Matches[1] -lt [version]$KeepVersion
} | Select-Object -ExpandProperty Name)
if ($taskShortcuts.Count) { Add-LocalRule 'local-storloom-old-shortcuts' 'Storloom superseded shortcuts' 'Storloom 旧版快捷方式' 'Exact old shortcut filenames only. Current and future shortcuts are retained.' @($taskLocal) $taskShortcuts 0 $false }
if (!$taskRules.Count) { throw 'No matching build outputs were found.' }
# This script generates reviewable rules only. It never scans user archives or deletes files.
$taskOutputPath = [IO.Path]::GetFullPath($Output)
if (!$taskOutputPath.StartsWith((Join-Path $taskRepo 'artifacts') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Write local rules beneath artifacts.' }
New-Item -ItemType Directory -Path (Split-Path -Parent $taskOutputPath) -Force | Out-Null
[ordered]@{version=1;rules=@($taskRules.ToArray())} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskOutputPath -Encoding utf8
Write-Output $taskOutputPath
