# Compact archives

New accounts recommend compact storage in 2.7. Choose the format when creating a reading archive or native backup. Existing data retains its format and is never converted or deleted automatically.

- **Compact archive (recommended)** packs small resources and writes messages directly into a persistent query index. Read it in the app without extracting the entire archive.
- **Compatible folder** keeps separate media and static HTML. Choose it for selective protection or static HTML compatibility.

Reading archives and native backups are independent outputs. Reading directly from local data does not require a native backup. Native backup needs no database key and preserves the original structure; parsing a reading archive still requires valid database access.

Reading updates skip unchanged databases, reparse changed databases, reuse existing messages and resources, and commit changed records. This is not universal row-level source-database capture. Database fingerprints and resource-directory scans still cost time. Missing media triggers a retry to fill resources. Native backups share content chunks across historical snapshots and shard their manifests by path hash. Restore lets you select a snapshot, reconstructs files into a new directory, and verifies them. Check client compatibility before importing the result.

Reading from a compact native backup currently materializes a complete temporary native copy for parsing. Plan space for that copy. Protected backups use an EFS workspace; successful completion removes the derived input, while interruption retains it for continuation.

Compact protection uses a random data key wrapped by a password. Password changes update the envelope instead of rewriting content. Resources use independent AES-256-GCM authentication, and the query database is also encrypted. Passwords require at least 8 characters and are never saved automatically. Updates retain the archive's protection, including new data. Encryption/decryption conversion creates a new copy and switches only after completion; the original stays available. Compact archives also support direct-key protection using 64 hexadecimal characters (256 bits). No password envelope or PBKDF2 derivation is used in that mode; an authenticated verifier rejects a wrong key before opening the index. The key is not stored. Direct keys cannot be changed by rewrapping a password; replacement requires re-encryption. Existing archives keep their selected mode during updates, resumes, copies and reorganization. Selective protection remains available through compatible folders.

Pause waits at bounded checkpoints and remains cancellable. Resume requires the same source snapshot and credentials. A changed source is refused rather than mixed into an inconsistent task. Explicitly abandoning a task leaves its published snapshot available. Unpublished packs are retained and can be omitted by reorganizing into a new copy. Protected reading ingestion still requires EFS for decrypted database and message spools; native packing does not require EFS.

The reading page exposes opening, updating and portable HTML export. Export can apply password protection. Advanced storage options expose statistics and reorganization. Reorganization streams current messages and retained native snapshots, omitting unused data and message revision history. It retains the source. Inactive query copies are reclaimed automatically; an open reader keeps its own snapshot. Query caches are rebuildable, and v3 replay reads pack metadata without reading all media payloads. Explicit full verification remains available; ordinary opens and updates do not silently verify every historical pack.

The new app reads ordinary reading copies and older packed archives. New compact archives require 2.7 or later. Export HTML for older applications or browsers. Do not delete internal packs or manifests manually.

Compact storage reduces small filesystem objects, but search indexes and history still occupy space. File-length statistics are not filesystem allocation measurements. Keeping the old reader usable during update requires a sequential copy of its query index, not a full history reparse. Allow space for the current index and the pending copy. Automatic worker selection considers storage, CPU and memory, with a manual 1-32 override; more workers do not guarantee higher throughput.

For an existing compatible folder, advanced storage options can create a separate compact reading from current local data. The old reading copy stays available; messages that exist only there are not merged. Import that completed reading copy instead when preserving all of its old content is required. Such imported archives continue updating through completed-reader import.

## Local management aliases

Use the edit icon next to the source heading to set an account alias and a source alias. An account alias is shared by its sources on the same platform; each source has its own alias. The original account ID and paths remain visible. These local display settings do not rename directories or change keys, archive identity, or message contacts. Clear an alias to restore the original display. The database-key account selector also shows these aliases.

Since 2.8, an idle native backup can select the next backup format even when a compatible-folder backup already exists. Creating a compact backup retains that previous backup. Unfinished tasks keep their original format.

## Work directory and task feedback in 2.9

Set the work directory in Preferences, Tasks, or in the task details before starting. An SSD is recommended when output is on a mechanical disk. Leave it blank to work beside the output. Tasks create their own directories under `Storloom/<source identity>`. The setting covers database and message spools and the private compact query index; it does not relocate the original source or the final packs. Protected plaintext spools still require EFS support. Successful publication copies and verifies the query index into the output, which remains independently readable after temporary work is removed. Reserve space for both the working index and its final copy.

An interrupted task retains its original work directory even if preferences change. Resume it with the same source snapshot and credentials. The setting applies to new tasks; it does not move unfinished work. Successful tasks remove their derived work where possible; a retained-work notice reports cleanup that could not finish.

Private compact writers use encrypted WAL for protected indexes, full synchronization and a bounded page cache. New indexes avoid unnecessary missing-record lookups; token and payload preparation can run in parallel while database publication remains serialized. The log reports cumulative pack preparation, durable pack write and index application time. Index time includes database work and storage waits, so it is not a pure I/O measurement. Final index copying and source parsing are additional costs. These figures help distinguish storage pressure from packing/encryption work.

Pause keeps the operation open at a safe checkpoint. Stop task immediately acknowledges the request, disables repeated stopping and shows indeterminate progress while the current step exits or rolls back. Waiting time is logged every five seconds until the operation actually finishes. Stopping retains completed steps and available resume checkpoints; resumability depends on the operation. No estimated completion percentage is invented for rollback. Required inputs disable starting until complete, and returning from a form retains its source and session draft. Credentials in drafts stay in memory only and are cleared when the window closes.
