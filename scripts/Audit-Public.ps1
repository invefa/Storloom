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
    if ($path -match '\.(ico|png)$') {
        $start = [Diagnostics.ProcessStartInfo]::new('git')
        $start.WorkingDirectory = $repo; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.ArgumentList.Add('show'); $start.ArgumentList.Add(":$path")
        $process = [Diagnostics.Process]::Start($start); $buffer = [IO.MemoryStream]::new()
        try {
            $process.StandardOutput.BaseStream.CopyTo($buffer); $process.WaitForExit()
            if ($process.ExitCode -ne 0) { throw 'Cannot inspect indexed image.' }
            $content = [Text.Encoding]::UTF8.GetString($buffer.ToArray()) + [Text.Encoding]::Unicode.GetString($buffer.ToArray())
            $lines = @($content)
            $readExit = 0
        } finally { $buffer.Dispose(); $process.Dispose() }
    } else { $lines = & git -C $repo show ":$path"; $readExit = $LASTEXITCODE }
    if ($readExit -ne 0) { throw "Cannot inspect indexed content: $path" }
    $content = $lines -join "`n"
    foreach ($marker in $markers) {
        $inspect = $content
        # Public product attribution is intentional; full private paths are still checked unchanged.
        if ($path -eq 'src/Storloom.App/ProductInfo.cs' -and $marker -eq $env:USERNAME) {
            $inspect = ($lines | Where-Object {
                $_ -notmatch '^\s*public const string Developer = "[a-zA-Z0-9_-]+";$' -and
                $_ -notmatch '^\s*public const string Repository = "https://github\.com/[a-zA-Z0-9_-]+/[a-zA-Z0-9_.-]+";$'
            }) -join "`n"
        }
        if ($inspect.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
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
