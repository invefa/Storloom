# Rule reference

Catalogs are local JSON data with `version: 1` and a non-empty `rules` array. Unknown properties, invalid enum values, duplicate IDs, invalid globs, negative sizes, and unknown path tokens are rejected. [JSON Schema](../rules/rules.schema.json) assists editors; the core engine performs runtime validation.

| Field | Meaning |
| --- | --- |
| `id` | Unique `^[a-z][a-z0-9-]{1,63}$` identifier |
| `name`, `description` | Human-readable fallback text; name required |
| `names`, `descriptions` | Optional dictionaries keyed by `en`, `zh-CN`, etc. |
| `action` | `Delete` (default), `Copy`, `Move`, or `Command` |
| `roots` | Non-empty absolute root patterns for file rules; empty for Command |
| `tool` | Command recipe: `NuGetHttpCache`, `NpmCache`, `PipCache`, or `UvPrune` |
| `destinationRoot` | Required for Copy/Move; new directory, never merged |
| `include`, `exclude` | Relative file globs; includes default to `["**"]`, excludes to `[]` |
| `processes` | Executable names without `.exe`; must be closed before execution |
| `enabled`, `recursive` | Both default to true; disabled rules can still be explicitly selected in CLI |
| `risk` | `Review` (default) or `Low`; GUI items start unchecked in both cases |
| `minimumAgeHours` | Default 168; both creation and last-write must be older; 0 through 876000 |
| `minimumBytes`, `maximumBytes` | Inclusive length bounds; default minimum 0, no maximum |

Root patterns support `*` and `?` within directory components, not `**`. File globs use `/` or escaped Windows separators: `*` matches within one component; `?` matches one character; `**` crosses directories; `**/*.log` includes top-level `.log` files. Excludes win. Rule overlap is assigned to the first matching rule in catalog order.

Available tokens: `%LocalAppData%`, `%AppData%`, `%ProgramData%`, `%UserProfile%`, `%Windows%`, `%SystemDrive%` (without trailing separator), and `%BackupRoot%` if configured. Arbitrary environment variables are not expanded. Copy/Move destinations may contain `{RootName}`, replaced by the matched source folder's final name. Multiple matches resolving to one destination cause a collision; use separate destinations or rules.

Example: copy old logs from one application into a **new** backup directory:

```json
{
  "$schema": "../rules/rules.schema.json",
  "version": 1,
  "rules": [{
    "id": "archive-example-logs",
    "name": "Archive Example logs",
    "description": "Copy logs older than 30 days; keep originals.",
    "names": { "en": "Archive Example logs" },
    "action": "Copy",
    "roots": ["%LocalAppData%\\ExampleApp\\Logs"],
    "destinationRoot": "%BackupRoot%\\ExampleApp\\Logs-archive",
    "include": ["**/*.log"],
    "exclude": ["**/*.lock"],
    "processes": ["ExampleApp"],
    "minimumAgeHours": 720,
    "risk": "Review"
  }]
}
```

`Copy`/`Move` rules transfer only the matched files and their relative paths. They are not complete-folder backups and do not include unmatched or empty directories. Use raw chat backup or the CLI full-folder transfer commands for complete folders. The GUI offers archive treatment inside Cleanup and archive. Scans skip read-only/system/hard-linked files and links; inspect diagnostics and rule matches before acting. Never use a partial selection as a claim of complete chat backup.

The GUI combines enabled sets from `rules/sets.json` with `data/rules.json` overrides by ID. Imports merge into local overrides; packaged rule files stay unchanged. Preferences controls the selected sets. Editing the file requires Reload. Changing the catalog/settings invalidates previously saved plans. Plans expire after 24 hours by default and are bound to the machine, user, and configuration. Copy/move execution uses a separate transfer plan with a fixed 24-hour lifetime.

## Local settings

Create `data/settings.json` beside the executable (CLI: `--settings FILE`). Keep this file private. `backupRoot` is used by rules; the chat page's backup destination is a separate UI preference. Default `allowedDrives` contains ready fixed drives, default `protectedRoots` is empty.

```json
{
  "allowedDrives": ["C:\\"],
  "protectedRoots": [],
  "backupRoot": "B:\\Backups",
  "planLifetimeHours": 24,
  "maximumCandidates": 500000
}
```

The drive letters above are placeholders. `allowedDrives`/`protectedRoots` restrict **Delete rules**; explicitly selected transfers have their own boundary and protected move-source checks. They are not a global disk access policy. No backup disk is selected automatically.

Deletion is permanent file-by-file deletion, not a recursive directory wipe. System directories, installed program directories, desktop/documents/downloads, credentials, repository internals, and broad roots are protected. Cache cleanup never assumes the entire application folder is disposable. File age, identity, metadata, link count, process status, and path boundaries are checked again before deletion. Permission failures are reported without taking ownership.


## Native tool recipes

```json
{
  "version": 1,
  "rules": [{
    "id": "prune-unused-uv",
    "name": "Prune unused uv cache",
    "description": "May also remove shared project environments; review before execution.",
    "action": "Command",
    "tool": "UvPrune",
    "roots": [],
    "risk": "Review"
  }]
}
```

Command rules must have Review risk, no roots, and no destination. They select an application-owned fixed recipe, not a shell program or arbitrary argument list. Unknown fields such as script, executable, or arguments are rejected. The scan records executable SHA-256 and any required npm script hash, without running cleanup. The executor recreates the recipe and holds executable/script files read-only while starting the child. Output is drained with a 1,048,576-character retained limit. Nonzero exits, timeout, and cancellation are recorded distinctly. Tool changes already completed are not rolled back.

| Recipe | Actual arguments |
| --- | --- |
| NuGetHttpCache | `dotnet nuget locals http-cache --clear` |
| NpmCache | `node <installed npm-cli.js> cache clean --force` |
| PipCache | `python -m pip cache purge` |
| UvPrune | `uv cache prune` |

These commands operate on each tool's configured cache as a whole, including configured relocated caches. Individual file exclusions and file age/size limits do not apply. uv prune can remove shared project environments. Use either a native recipe or a file rule for the same cache to avoid duplicate work. Private settings can supply `toolPaths` (dotnet/node/python/uv) and `commandTimeoutSeconds` (default 300, range 1..3600). The application never elevates or invokes a shell for these recipes.

Official references: [NuGet](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-locals), [npm](https://docs.npmjs.com/cli/commands/npm-cache/), [pip](https://pip.pypa.io/en/stable/cli/pip_cache/), [uv](https://docs.astral.sh/uv/concepts/cache/).

## Rule-set categories and local archive treatment

The default selected sets are system, browsers, and development. Optional sets add desktop-apps, communication-cn, media-cn, and native-tools. `builtin.json` contains the system category, not every category. CLI defaults load the manifest defaults; `--catalog` loads exactly that file.

A GUI archive treatment does not rewrite the rule definition. It creates a verified transfer plan from the same scanned, checked matches. Destinations are separated by rule ID, source-root hash, and a fresh run directory. It cannot convert a Command rule to a file archive. Scan plans save candidate matches; reselect and review treatment after loading a saved plan. File rule links remain blocked; explicitly name a verified physical cache leaf in local overrides instead of following a broad junction.

## Native cache measurements

A Command rule accepts an optional measurement object. The default is `{"mode":"Tool"}`. Storloom runs a fixed read-only location query (`dotnet nuget locals http-cache --list`, `node <npm-cli.js> config get cache`, `python -m pip cache dir`, or `uv cache dir`), parses the canonical local directory, and scans its logical size. npm measures `_cacache`, matching the cleanup recipe. These queries have a 30-second upper limit, bounded output, and cancellation. They never accept arbitrary executable names, shell expressions, or output parsing code from a rule. A missing cache is zero; an inaccessible, linked, unsupported, or timed-out measurement is unknown.

For a verified relocated cache, choose explicit roots:

```json
"measurement": {
  "mode": "Directories",
  "roots": ["%LocalAppData%\\ExampleApp\\Cache"]
}
```

Explicit measurement roots are read-only, use the same token/glob resolver, reject links and entire drives, and must not be confused with deletion roots. File rules cannot define measurement. Overlapping measurement roots are counted once. Measurements do not authorize extra cleanup paths; the native tool still owns its cache scope.

The executor measures again immediately before and after running the tool. The result records beforeBytes, afterBytes, and freedBytes. Differences use logical file sizes, can be negative if concurrent writes grew the cache, and are not filesystem allocated-space guarantees. Partial/unknown scans produce no released-byte claim. File deletions use the bytes of files actually removed; archive copies report zero source bytes released.

Adding rule fields changes catalog hashes; rescan old cleanup plans before applying them.

For pip only, `{"mode":"ToolReport"}` calls `python -m pip cache info` and parses its HTTP/wheel size totals. This native report rounds decimal sizes, so previews use a `~` prefix. Cleanup still performs exact directory measurements before and after execution; it never subtracts rounded estimates to claim released bytes. Unsupported native report formats remain unknown. Other tools currently use Tool directory discovery or explicit Directories measurements.
