# Archive library

New accounts in 2.7 default to direct compact archives. See [Compact archives](compact-archives.md) for format selection, native snapshots, protection and maintenance. The import path below remains available for existing reading copies.

Open **Chat archives > Tools > Storage maintenance** to manage the packed storage format. Existing backups and reading copies remain available through their original entries. Nothing is migrated automatically.

## Bulk recovery and relocation

**Find archived data** scans a selected backup root for compatible completed readers, then lets you review and select registrations. Skipped paths and scan limits are reported. Original backups still use the verified **Connect existing data** flow. Reconnecting moved metadata relinks a matching registration when its previous path is missing, retaining other valid copies. See [archive experience design](archive-experience-design.md) for scenarios and future priorities.

## Configuration packages, comparisons and reports

Use **Tools → Back up configuration** for a `.storloom-config` package containing selected registrations, aliases, application/reader preferences and optionally protected database-key files. Chat data, cleanup rules and unsaved forms remain separate. Package encryption defaults on and is mandatory for keys. Protected key files retain their original passwords; the package password does not replace them.

**Restore configuration** unlocks the package and previews selected scope and source locations. Matching registrations are retained by default, or explicitly updated while unrelated entries remain. Selected preferences update; machine executable paths, rule-set selection and custom-theme files are not imported. Pre-restore copies live in local `configuration-recovery`. Stale previews are rejected; cancellation/failure during writes attempts rollback. Import never executes jobs, moves archives or unlocks chat data.

**Compare archive versions** is available on material and history views. Pick the other archive entry. A supplied credential is reused when possible, with another local prompt for a different credential. Reports compare indexed counts and time ranges, not every message or resource byte, and do not establish integrity. Unknown folder-reader fields remain unknown.

The material **Integrity report** tab provides **Check archive integrity**, the previous report and version comparison. Checks explicitly scan data and report scope, outcome, time and checked amounts, with cancellation and JSON export. Original-backup verification uses the same report view. Plain folders lacking trusted original digests remain partial. Authenticating stored records cannot recover missing original media. Changed snapshots are flagged. Reports default to local `archive-reports`, preserving read-only archives; export explicitly when a report should travel with the archive.

Configuration packages include reader preferences saved by the desktop application, not standalone HTML state stored inside a browser. Original backups without a reading index have unknown message counts, rather than being reported as empty conversations.

## Protocol and provenance

`archive.metadata` is JSON beside `index.html`. `version` identifies metadata schema (currently 2); `exportProtocolVersion` identifies the independent Storloom export protocol (currently 1); `storageFormatVersion` identifies internal storage; `exporterVersion` records the exporting/converting application version. Protocol 1 represents the existing export pipeline and compatible reading format. Unknown schema/protocol versions are rejected without rewriting the archive.

`origin` describes the original client source; `history` records export, native backup, import, copy, repack and decryption events and their direct parents. Conversion inherits history and appends an event; retrying the same revision does not duplicate it. History is visible in the material history tab. Existing `archive.storloom.json` entries remain readable and migrate to the new filename after validation on save.

Recovered registrations record only verified provenance and carry a `recovered` flag. Their timestamp is the recovery time; an unknown original exporter version remains empty. Original export dates and missing task logs are not invented. Reconnecting metadata after configuration loss recovers account grouping, source descriptions and saved operation history.

## File entry points and recovering registrations

Choose **Chat archives > Connect existing data**, then select a file rather than deciding which directory level to use:

- New reading copies, compact original backups, and compact imports/copies/repacked archives expose `archive.metadata` at the outer level. Prepared independent HTML also provides `index.html`; internal objects remain in `archive-data`.
- For an older compact archive, select its internal `archive.json` or outer `index.html`. Storloom resolves `archive-data` to the outer archive and avoids duplicate registration.
- For an older folder reader, select `messages.html` or `vault.storloom.json`. Folder-based original backups continue to use their existing backup-manifest JSON.

Metadata identifies the client and account for grouping. Older readers without that information offer an association form: enter the QQ account or WeChat account-directory identifier, or leave it blank to keep independent material. Right-click an existing independent entry and choose **Associate account** to amend it. Saving writes portable metadata without re-exporting, re-encrypting or requesting the reading password; actual reading still uses the existing password.

Metadata contains archive identity, client/account labels, relative data location, original provenance and operation history. It contains no passwords, keys, chat content or complete machine task settings. Provenance may include the original client path: it is public descriptive data, never restore/update authorization. Relative data lookup survives moves and does not depend on the old source path. Association does not turn a reader into original client data: updating still requires the real client source or an original backup.

The list is an **Archive library**: known client/account combinations group their sources, while unassigned material remains independent. Internal directory names are not treated as accounts. The context menu edits display aliases or removes the current source/group registration. **Removing a registration retains archive, backup and reading files on disk.** Complete or abandon an unfinished task before removing its registration.

Reconnecting restores library entries and account grouping. Existing historical readers and original backups can be connected individually; material history shows registered versions. A reader alone cannot reproduce lost task logs, export settings or the database key library. Those require a backup of the application's local data directory.


## Import and update

Choose **Import reading copy**, select a completed reading copy and a new archive directory, then review the source and destination. The source is preserved. To resume an interrupted import, select the same source and archive directory. A changed source is rejected while an import is incomplete.

For later updates, choose **Import / resume** and a completed, newer reading copy of the same account. The source transcript is scanned to detect changes; unchanged messages are not reindexed and unchanged resource files reuse their stored file identities. This entry does not yet read incremental changes directly from an original backup. A first import, cache rebuild or new source scan is separately reported and must not be mistaken for a constant-time operation.

Messages, search postings, resource locators and checkpoints are committed together. Original message JSON, unknown fields and source files are retained in immutable packs. Small files share packs, while large files are chunked. Earlier message revisions remain in prior commits; the reader displays the latest revision.

## Read, search and backup

Select an archive and choose **Open reading copy**. The built-in reader loads current pages and media directly from the store. It does not extract the whole archive. Text-only global searches use the persistent search index; additional filters retain the compatible page-search path. Reference jumps use indexed message identifiers.

**Incremental backup** targets a new directory or that archive's existing backup. It copies and verifies new commit files and data packs, then publishes the new snapshot. An interrupted backup first finishes its captured snapshot. Run backup again to collect changes made afterward. Query caches are not copied and rebuild when a backup is first opened.

**Verify backup now** performs an independent full archive verification with commit and byte totals. Opening and updating do not automatically read every historical pack. Verification checks message revisions as well as resource chunks; its revision count can exceed the reader's current message count.

## Passwords and exports

Select encryption when creating an archive. Encrypted source reading copies require encrypted destinations. Private packs and query indexes are encrypted; passwords are not automatically saved. Passwords require at least 8 characters. Changing the archive password rewraps its master key without rewriting all packs. Older backups keep their previous password until updated. This does not revoke leaked copies of the master key.

New stores currently support whole-archive encryption. Granular protection remains available for compatible exported reading copies; mixed public/private domains inside a packed archive are not enabled.

**Export reading copy** creates current pages, reference maps and JSONL from the current query state, while reusing stored media. Choose a new directory, or the same interrupted export directory before changing the source archive. Completed files are authenticated against the export checkpoint on resume. An unrelated or modified destination is rejected.

The export dialog also offers password protection. Encryption starts after export completes, so interrupted export or encryption can leave plaintext in the destination. Resume unfinished encryption through **Tools > Protect reading copy**, using the chosen export password. The archive library and original sources are unchanged. Plaintext reading copies can be read by anyone with disk access.

Import, backup, export, encryption and verification use the existing pause/cancel controls. Completed checkpoints survive cancellation. Closing a reader releases its update lock; close destination readers before importing new content or changing the password.

## Current limits

This release provides a serial migration and backup baseline. Worker recommendations for existing backup/export/encryption tasks are not a claim that packed-store migration or backup runs on eight workers. Two-disk, cold-cache, low-memory and power-loss durability benchmarks are pending. Process-interruption recovery is covered by synthetic tests; a single storage device is still not a substitute for an independent backup.
