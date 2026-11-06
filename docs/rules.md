# Rule reference / 规则参考

Catalogs are local JSON data with `version: 1` and a non-empty `rules` array. Unknown properties, invalid enum values, duplicate IDs, invalid globs, negative sizes, and unknown path tokens are rejected. [JSON Schema](../rules/rules.schema.json) assists editors; the core engine performs runtime validation.

| Field | Meaning |
| --- | --- |
| `id` | Unique `^[a-z][a-z0-9-]{1,63}$` identifier |
| `name`, `description` | Human-readable fallback text; name required |
| `names`, `descriptions` | Optional dictionaries keyed by `en`, `zh-CN`, etc. |
| `action` | `Delete` (default), `Copy`, or `Move` |
| `roots` | Non-empty absolute root patterns; each root is checked independently |
| `destinationRoot` | Required for Copy/Move; new directory, never merged |
| `include`, `exclude` | Relative file globs; includes default to `["**"]`, excludes to `[]` |
| `processes` | Executable names without `.exe`; must be closed before execution |
| `enabled`, `recursive` | Both default to true; disabled rules can still be explicitly selected in CLI |
| `risk` | `Review` (default, initially unchecked) or `Low` |
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
    "names": { "zh-CN": "归档 Example 旧日志" },
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

`Copy`/`Move` rules transfer only the matched files and their relative paths. They are not complete-folder backups and do not include unmatched or empty directories. Use the dedicated transfer page or raw chat backup for complete folders. Scans skip read-only/system/hard-linked files and links; inspect diagnostics and rule matches before acting. Never use a partial selection as a claim of complete chat backup.

GUI imports replace the local catalog in `data/rules.json`; add built-in rules to your new catalog first if you wish to retain them. Editing the file requires Reload. Changing the catalog/settings invalidates previously saved plans. Plans expire after 24 hours by default and are bound to the machine, user, and configuration. Copy/move execution uses a separate transfer plan with a fixed 24-hour lifetime.

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

中文：规则只描述数据匹配，不执行脚本。导入后先扫描，检查“动作、根目录、目标目录、候选文件、跳过原因”。`Move` 仅对命中的文件执行“复制、整体核验、再次检查、删除源文件”；完整聊天备份请使用聊天页的原始目录备份。`Low` 只影响界面默认勾选，不代表任何文件都可无条件删除。
