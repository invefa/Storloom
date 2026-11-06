[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$paths = & git -C $repo -c core.quotepath=false ls-files
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate the Git index.' }
$markers = @($env:USERPROFILE, $env:USERNAME, $env:COMPUTERNAME, $repo,
    (& git -C $repo config --get user.email)) | Where-Object { $_ -and $_.Length -gt 5 }
$failures = [Collections.Generic.List[string]]::new()
foreach ($path in $paths) {
    if ($path -match '(^|/)(artifacts|data|bin|obj|secrets|\.local|\.git)/' -or
        $path -match '\.(db|db-wal|db-shm|key|pem|pfx|jsonl|log|trx|zip|exe|dll|pdb)$') {
        $failures.Add("Forbidden indexed file: $path"); continue
    }
    $lines = & git -C $repo show ":$path"
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect indexed content: $path" }
    $content = $lines -join "`n"
    foreach ($marker in $markers) {
        if ($content.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $failures.Add("Local identity/path in: $path"); break
        }
    }
    if ($content -match '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----' -or
        $content -match '(?i)(?:gh[pousr]_[a-z0-9]{30,}|github_pat_[a-z0-9_]{30,}|sk-proj-[a-z0-9_-]{30,})') {
        $failures.Add("Credential pattern in: $path")
    }
}
if ($failures.Count) { $failures | Write-Output; throw 'Public-source audit failed.' }
Write-Output "Inspected $($paths.Count) indexed files: no local identity, private artifacts, or credential patterns found."
