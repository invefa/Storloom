# AI rule guide

Storloom accepts data-only JSON catalogs. It has no embedded AI account, API key, prompt execution, or cloud upload. Copy the prompt below into an AI of your choice, provide only the paths and file metadata you intend to disclose, and save the returned JSON locally.

## Prompt to copy

You are authoring a Storloom rule catalog, version 1. Read the supplied rules.schema.json and rules.md. Return only strict JSON, no Markdown fence, commands, or script code.

Ask for missing source/destination intent instead of guessing destructive paths. Use stable lowercase IDs, explicit roots and relative include/exclude globs, a clear name/description, and `risk: "Review"`. Include `names` and `descriptions` translations for English and Simplified Chinese if possible. Default to Copy for archiving. Delete only documented disposable cache/log files that the user explicitly requested; never classify chat databases, account data, credentials, documents, development sources, installers in active use, or system components as cache.

Use supported path tokens where possible. Never use a whole drive, user profile, AppData, or application installation directory as a deletion root. Respect retention periods, relevant process names without .exe, and exclusions. Copy/Move needs a new non-overlapping destination; `%BackupRoot%` requires a private settings.json value. `{RootName}` is available for matched-root names; avoid destination collisions. Globs select files only, and a rule archive is not a full account backup. Do not include secrets, machine IDs, user account numbers, or personal facts in rule IDs/descriptions.

My requested operation and folders:
[Describe the source, what to keep, age/size limits, Copy/Move/Delete intent, and a destination chosen by the user.]

## Review workflow

1. Validate the output against [the schema](../rules/rules.schema.json) in an editor.
2. Import it from File > Import rules. Imports merge by ID into private overrides; enabled packaged sets remain available.
3. Scan. Read the paths, action, retention, process restrictions, diagnostics, and Copy/Move destinations. Uncheck anything uncertain.
4. Confirm the concrete plan. Keep journals/backup manifests private. A new scan is required after configuration changes or expiration.

AI output does not grant permission to delete. The UI confirmation and engine checks remain in force. Runtime guards provide additional protection but cannot decide whether a personal file matters to you.

## Native cleanup instead of file deletion

If the user wants the producing tool to manage its cache, use action `Command`, risk `Review`, roots `[]`, and one supported tool enum: NuGetHttpCache, NpmCache, PipCache, or UvPrune. Explain the whole-cache scope and the uv shared-environment effect. Do not invent executable/script/arguments fields or embed shell commands. The app owns fixed recipes and the user reviews the actual resolved command. For custom tools outside this list, request a reviewed provider addition rather than disguising arbitrary code as a cleanup rule.
