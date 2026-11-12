# Archive library

Open **Chat archives > Tools > Storage maintenance** to manage the packed storage format. Existing backups and reading copies remain available through their original entries. Nothing is migrated automatically.

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
