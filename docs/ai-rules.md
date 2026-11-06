# AI rule guide / AI 规则指南

Storloom accepts data-only JSON catalogs. It has no embedded AI account, API key, prompt execution, or cloud upload. Copy the prompt below into an AI of your choice, provide only the paths and file metadata you intend to disclose, and save the returned JSON locally.

## Prompt to copy

You are authoring a Storloom rule catalog, version 1. Read the supplied rules.schema.json and rules.md. Return only strict JSON, no Markdown fence, commands, or script code.

Ask for missing source/destination intent instead of guessing destructive paths. Use stable lowercase IDs, explicit roots and relative include/exclude globs, a clear name/description, and `risk: "Review"`. Include `names` and `descriptions` translations for English and Simplified Chinese if possible. Default to Copy for archiving. Delete only documented disposable cache/log files that the user explicitly requested; never classify chat databases, account data, credentials, documents, development sources, installers in active use, or system components as cache.

Use supported path tokens where possible. Never use a whole drive, user profile, AppData, or application installation directory as a deletion root. Respect retention periods, relevant process names without .exe, and exclusions. Copy/Move needs a new non-overlapping destination; `%BackupRoot%` requires a private settings.json value. `{RootName}` is available for matched-root names; avoid destination collisions. Globs select files only, and a rule archive is not a full account backup. Do not include secrets, machine IDs, user account numbers, or personal facts in rule IDs/descriptions.

My requested operation and folders:
[Describe the source, what to keep, age/size limits, Copy/Move/Delete intent, and a destination chosen by the user.]

## Review workflow

1. Validate the output against [the schema](../rules/rules.schema.json) in an editor.
2. Import it from Rules and cleanup. Imports replace the private catalog; combine with existing rules beforehand if needed.
3. Scan. Read the paths, action, retention, process restrictions, diagnostics, and Copy/Move destinations. Uncheck anything uncertain.
4. Confirm the concrete plan. Keep journals/backup manifests private. A new scan is required after configuration changes or expiration.

AI output does not grant permission to delete. The UI confirmation and engine checks remain in force. Runtime guards provide additional protection but cannot decide whether a personal file matters to you.

## 中文提示词

请根据随附的 rules.schema.json 和 rules.md，为 Storloom 生成 version=1 的严格 JSON 规则目录，只返回 JSON。使用明确的根目录、相对 include/exclude 模式、合理保留时间、相关进程名称（不带 .exe），并设置 risk=Review。归档默认使用 Copy；只有我明确要求的、已确认可重建的缓存或日志才能 Delete。不要把聊天数据库、账号、凭据、文档、源码、系统组件或整个应用目录当作缓存。不要猜测磁盘用途或迁移目标。Copy/Move 的目标必须为新的非嵌套目录；使用 %BackupRoot% 前提醒我在本地配置。避免多个根目录映射到相同目标。规则只归档命中文件，不能代替完整账号备份。名称和说明提供 en、zh-CN 翻译，不写入账号和个人信息。

我的需求：[填写目录、清理或归档范围、必须保留内容、时间/大小条件和目标目录。]

生成后仍须在本机导入、扫描、审核候选与目标，最后确认执行。只向 AI 提供愿意披露的目录和元数据，无需发送聊天正文或密钥。
