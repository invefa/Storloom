# Archive storage v2 proposal

Status: optional archive-library import, reading, indexed text search, incremental backup, verification and reading-copy export are connected in 2.0.0. No automatic migration or replacement of existing archives. Running tasks are unchanged.

## Implementation status

- Legacy format: verified-copy index identity rebinding, prepared statement reuse, and transactional bootstrap checkpoints using exact UTF-8 byte offsets. Rows and their cursor commit together.
- New storage primitives: bounded immutable packs, content verification, parent-linked commits, HEAD-last publication, and replay of only the unapplied suffix. Failure injection covers unpublished orphan packs.
- Query prototype: persistent SQLite projection, atomic data/search/cursor application, and keyset pagination. Original JSON is retained. Generated Unicode tokens plus exact-text verification support Chinese short queries, mixed scripts, and emoji. Position-free FTS5 postings still require larger space and tail-latency benchmarks.
- The archive library connects import/resume/update, chunked files, direct reading, indexed text search and reference lookup, incremental backup, independent verification, password changes and current-state reading-copy export. The storage API stays internal. Source updates currently come from completed reading copies; direct original-backup delta adapters and mixed protection domains remain future work.
- Failure tests currently cover in-process interruption and retry, not power-loss consistency. NTFS/exFAT directory-entry durability, redundant root records, corruption recovery, and cross-process concurrency still require validation. Import remains opt-in, preserves original sources, and is not a substitute for independent backups.
- The encrypted query prototype now uses maintainer-provided SQLite3MC.PCLRaw 2.6.1, isolated from system SQLite used by legacy adapters. Tests cover encrypted reopen, wrong-key rejection, and rollback journals without plaintext. Protected queries require a binary key, with no plaintext fallback. Native provenance and licenses are recorded and included by the packaging script.
- The key prototype uses a random master key, PBKDF2-SHA256 password wrapping, separate domain keys, and AES-GCM authenticated payloads. Password changes rewrap only the master key, leaving packs unchanged. A compromised master key requires data re-encryption; password changes cannot revoke older keyring copies. New-store creation offers whole-store encryption; granular protection remains available for compatible reading exports, rather than mixed protection domains within the store.

### Second stage: core integration

- Migration imports messages, then preserves exact reader file bytes including original JSONL, unknown fields, media, and other files. Sources are never changed or removed. Protected sources use authenticated decryption streams without a full plaintext staging copy, and cannot target an unprotected store.
- Message cursors use exact UTF-8 byte offsets. File cursors identify the source file and chunk offset. Cursors, messages, resource locators, and file progress commit together and can rebuild the query cache. Source inventories and versions must match when resuming; changed snapshots are rejected.
- Current internal parameters are 1 MiB file chunks and roughly 8 MiB raw-resource batches, with small files packed together. They remain subject to measurement. Chunks are deduplicated within their protection domain, using keyed content IDs for private data. The query cache stores locators rather than duplicate media bytes.
- File streams support seeking. An archive-wide cache reuses up to two authenticated decoded packs, with a 16 MiB resource-byte budget, across concurrent resource streams. Stream chunk buffers clear on disposal; shared cached resource buffers clear on eviction or archive close.
- Reads still verify and decode the pack containing the requested chunk. Amplification and tail latency for many small random resources across different packs still need optimization and measurement; media preview performance is not yet established.
- Backup copies only the commit suffix after the destination head and excludes the query cache. New objects are copied and verified against content digests before publishing destination HEAD. A resumed task finishes its captured commit and wrapped-key metadata first; a later backup collects subsequent changes. Interrupted updates retain the previous readable backup.
- Explicit full verification reports commit and byte totals and supports pause/cancellation. Normal opening, message updates, and incremental backups do not automatically verify all historical packs.
- Synthetic tests cover public/encrypted migration, message/file resume, partial-final-chunk boundaries, byte-exact restoration, chunk deduplication, cache rebuild, source-change refusal, protected legacy readers, incremental backup, and corruption. Migration and backup currently provide a serial baseline; parallel tuning, two-disk measurements and original-backup source adapters remain incomplete. Desktop tests exercise the library, migration/export options, public/encrypted direct reading and native search pagination.

References: [SQLitePCLRaw maintainer encryption guidance](https://github.com/ericsink/SQLitePCL.raw/wiki/SQLite-encryption-options-for-use-with-SQLitePCLRaw), [native package maintainer documentation](https://github.com/utelle/SQLite3MultipleCiphers-NuGet), [SQLite FTS5](https://www.sqlite.org/fts5.html).

### User-facing integration

See [Archive library](../docs/archive-library.md). Native search batches are bounded to 100 hits and use tuple cursors. Reference jumps use an identifier index instead of loading a full conversation reference map. Portable exports regenerate current pages, reference maps and JSONL; an authenticated append-only completion journal supports retry without rewriting an ever-growing checkpoint for every file. Export offers optional post-export protection through the established vault implementation.

### Preliminary performance observations

Single-run synthetic Release measurements on one local disk, not production or cross-disk guarantees. Adding 100 records after one million historical records replays one commit. Ten-million-record, cold-cache, repeated p50/p95, and two-disk validation remain pending.

| Measurement | Observation |
| --- | --- |
| Legacy wrapper, one million message-key inserts | 24.130 s |
| Prepared statements, same keys | 6.102 s, about 3.95x |
| Initial packs plus message/search projection, one million messages | 36.461 s |
| Append 100 messages and update message/search projection | 0.020 s |
| First 100 common Chinese substring hits, ordered by time | 506.693 ms |

New-format measurements use the public domain and exclude source database scans, media, password derivation, and encryption. Encryption correctness has separate tests; encrypted performance needs separate measurement. Global chronological sorting of very common search hits remains an optimization target.

## Decision

Separate the long-lived archive from its reading exports. Immutable manifests and object packs preserve recoverable source data and message changes. A persistent SQLite query database maintains normalized messages and search indexes. HTML and portable reading packages become derived exports.

Performance is a requirement alongside correctness: ordinary updates must not clone a complete reader, import every old message into a fresh index, or decrypt an entire archive for viewing. Do not gain speed by omitting authentication or deleting original evidence.

Do not use one giant JSONL file, a repeatedly rewritten compression archive, or one filesystem file per message as the primary layout.

## Evidence in the current code

`ChatReadingUpdate.cs` imports historical messages when its update index is unavailable and rewrites the combined transcript for changes. Its file-identity reuse check does not survive ordinary generation copies. `ChatArchiveWorkflow.cs` copies the previous reader into a work area. `ChatIncrementalBackup.cs` already references some unchanged resources through manifests, but copies complete database files.

The v2 format addresses these separately. Fixing the existing index reuse and statement preparation overhead should precede the full format migration.

## Layout and authority

```text
archive/
  format.json       # Minimal format metadata and archive UUID
  roots/            # Immutable commit manifests and redundant commit pointers
  packs/            # Sealed packs of events, original payloads and resource chunks
  snapshots/        # Optional query/index checkpoints
  indexes/          # Optional portable query indexes
  tasks/            # Plans and source-snapshot-bound cursors
  staging/          # Uncommitted, resumable writes
```

Committed manifests and their referenced packs are the recovery authority. SQLite stores normalized query views, relationships and full-text indexes, tagged with their applied commit. It is persistent and incrementally maintained, even though it can be rebuilt if damaged. Logical identity is independent of path, inode or Windows file ID.

Do not flatten all historical objects into every new manifest. Use a persistent object map with shared unchanged nodes, or checkpoints plus bounded delta manifests. Update affected nodes and root references only. Object locations include pack ID, offset and length; otherwise giant manifest rewrites recreate a whole-archive bottleneck.

Unencrypted format metadata contains no contact names, conversation titles, message text or keys. Private manifests, object maps and checkpoints are encrypted. Opaque filenames do not hide all size, count or modification-time information.

## Stable records

- Stable archive/account/conversation IDs survive directory moves. Source paths are locations, not account identities.
- Message identity uses reliable adapter/account/conversation/native IDs. When absent, retain source-row provenance and conservative fingerprints with documented limits. Identical text is not sufficient to merge messages.
- Revisions retain text, type, replies, resource references, original payload references, parser version, source snapshot and observation time. Withdrawals, edits and reactions are events/revisions, not destructive replacement of evidence.
- Names, remarks, scoped display names and avatars have versions and provenance. Preserve actually observed message display information; do not invent historical effective dates from a current profile.
- Alternative image sources and derived audio/thumbnail renditions are related resources. Derived material inherits protection requirements.

Initial query tables include `messages`, `message_revisions`, `conversations`, `identity_versions`, `resources`, `message_resources`, `source_snapshots`, `import_cursors`, `applied_commits` and `search_documents`.

## Source import and raw backup

Pin a consistent source snapshot for each task. Keep cursors, format versions and verification information per source shard. Use reliable native sequences/change records where available, including overlap checks for late data. A maximum timestamp alone misses historical edits, withdrawals, reactions and profile changes.

Some adapters may still need to scan a changed source database. Report this explicitly and time it separately. This fallback must not also force a scan of all archived history or a full search-index rebuild.

Sources without reliable filesystem change tracking may require a complete metadata enumeration, and explicitly requested full verification reads whole batches. Report source enumeration, source-data scans and archive-index work separately. Archive-side incremental processing does not imply delta-only source reads.

Raw snapshots preserve byte-level recovery information. Chunked large files and manifest references reuse unchanged data. Re-encryption of a source database may alter many ciphertext blocks, reducing deduplication; do not promise efficient block reuse for every provider. Database and journal files must belong to one consistent source snapshot.

Track parser versions independently. Reprocess affected retained payloads/shards where possible. Whole-source reparsing is an explicit maintenance operation when unavoidable.

## Object packs

Benchmark initial sealed-pack targets of 16-64 MiB and large-resource chunk targets of 1-4 MiB. These are experimental parameters. Bound random-read amplification, recovery scope, memory and filesystem object counts before freezing them.

Pack small objects together. Published packs are immutable. Independently framed objects/chunks carry integrity information and a range lookup index. Avoid a single compression stream that requires decoding a whole pack for one object. Already compressed images/video normally bypass further compression; text and structured payloads can use independent compressed frames.

Deduplicate within a protection domain. Private object identifiers use keyed digests or opaque IDs rather than exposing plaintext content hashes in public manifests. Do not automatically share publicly readable objects across public/private domains.

Garbage collection, repacking and compaction are separate resumable maintenance tasks over known committed reachability. Do not rewrite the whole archive after each update or automatically delete original backups and old-format archives.

## Persistent indexes, reading and search

Maintain message identity, conversation order and FTS5 search documents in batched SQLite transactions. Reuse prepared statements and bound caches. Avoid a prepare and a transaction for every record.

FTS5 is a candidate search foundation; Chinese tokenization still needs explicit validation. Evaluate CJK bigrams plus word tokens, with supplementary indexes or an explicitly scope-limited fallback for single characters. Normal global queries must not conceal a full-history scan. Test short words, individual characters, punctuation, emoji, mixed scripts, substring behavior and index-space amplification. See the [FTS5 documentation](https://www.sqlite.org/fts5.html).

Use keyset queries over `(conversation_id, timestamp, message_id)`, not huge OFFSET scans. Resolve quoted-message jumps by stable ID. Separate requested page size from the rendered viewport. Return bounded search results with continuation and forward/backward navigation. Include private search domains only after unlocking.

Read media by indexed chunk ranges. Search results, thumbnails, transcodes and preview caches participate in the protection policy.

## Disks, concurrency and backups

Keep the authoritative archive on the selected volume. Offer a reconstructible local SSD query cache for slower/removable archive volumes to avoid random index writes there. Losing the cache cannot lose archive data. Sensitive caches must also be encrypted.

Use a bounded read -> normalize -> compress/encrypt -> pack -> batch-database-write pipeline. Independent stages may use 1-8 workers, chosen according to storage, CPU and memory. A single batched database writer is usually preferable to competing writers. Apply backpressure instead of accumulating an entire archive in RAM.

WAL is a possible local SQLite journal mode, not a network-filesystem synchronization protocol. Generate consistent database checkpoints through supported APIs. Do not copy only a live `.db` file. Stepping the Backup API is not the same as a changed-pages-only backup. See [WAL](https://www.sqlite.org/wal.html) and the [Backup API](https://www.sqlite.org/backup.html).

Apply commit deltas to the persistent query cache. Create portable index checkpoints according to recovery-cost thresholds or explicit maintenance, rather than copying the entire database for every update. Secondary archive backups normally transfer new immutable packs and manifests. Restore from a checkpoint plus later event deltas; explain the initial reconstruction cost if no checkpoint exists.

## Commit and resume protocol

1. Acquire one archive writer lease and record the pinned input snapshot, protection plan and cursors.
2. Write new packs and event deltas into staging. Checkpoint at verifiable object/batch boundaries.
3. Verify and durably flush packs, then seal a new immutable root manifest. Use checksummed, sequenced redundant pointers rather than relying on one overwrite never tearing.
4. Apply the commit to the query database transactionally, including FTS and its applied-commit marker. A pointer file and SQLite are not one atomic transaction; handle divergence explicitly.
5. If the root committed but the index lagged, replay missing deltas. Readers may serve the previous complete view with an index-catching-up indication. Never label a mixed view complete. All visible protection domains must reach the same visible commit.

Mark the task complete only when data and required indexes reach the target commit. Recover from complete manifests; continue or collect uncommitted staging. Pause at bounded batch boundaries. Cancel retains committed work and cursors. A cursor cannot be silently applied to a different input snapshot.

Serving a previous complete view requires actual version rows/read snapshots and an explicit visible-commit marker, not a single overwritten current row. Filter query and search results by visible revisions. Individually committed domain databases do not establish cross-domain atomicity.

## Encryption

Generate a random archive master key. A versioned, parameterized password KDF produces a wrapping key; domain keys are derived or wrapped under the master key. Do not make the password the direct long-lived key for every file.

Reuse unlocked keys within a session rather than repeating a password KDF for each stage/file. Bound memory and clear keys on lock/exit; session reuse is not automatic password persistence.

A normal password change can rewrap keys. Rotation after actual master-key compromise requires re-encryption, a different operation. Key inspection/export is explicit; passwords and original database keys are not saved automatically.

Use authenticated chunk encryption, with the reviewed AES-256-GCM direction as a candidate. Never repeat a key/nonce pair for a new write. Bind archive/domain/object version/chunk sequence in associated data. Reuse authenticated existing ciphertext without relaxing verification rules.

Evaluate a maintained encrypted SQLite implementation, initially SQLCipher, for private query databases. Validate indexes, journals and temporary storage together. Windows builds, licensing, FTS5, package size and compatibility are release gates. Encrypting only message fields is insufficient if search indexes remain plaintext. See [SQLCipher design](https://www.zetetic.net/sqlcipher/design/).

Read protected archives through on-demand memory decryption rather than a fully restored plaintext work directory. Source adapters are a separate concern: adapters that still require decrypted temporary databases must retain an explicit safe path until page-based reading exists, with no ordinary plaintext fallback.

Initially use public/private domains rather than one database per conversation. Persist selection rules for conversations, types, senders, time and content. Offer shared-object protection and strict affected-pack repacking with visible scope. A private copy cannot hide identical content that remains in a public message.

Distinguish protection of the active view, history, raw backups and derived caches. Migration statuses must distinguish unprotected, migrating, ciphertext-created-with-plaintext-copies, and completed-in-target-scope. Old copies are not automatically removed. Creating ciphertext does not prove all plaintext disappeared, and deleting files is not a media-sanitization guarantee.

Moving formerly public data into a private domain cannot merely delete public query rows: old pages, FTS structures and journals may retain content. A fresh public query database and explicit old-cache handling may be necessary. This security-maintenance cost can exceed the content delta; estimate and report it separately rather than omitting it to claim fast partial encryption. Choosing protection at archive creation reduces later plaintext migration.

## Product flow and exports

Keep create/update/open archive, export reading and protection settings as the main actions. Updating the archive does not require first generating a complete HTML reader. The native reader shares the query/decryption layer.

Retain compatible HTML export as a derived path. Incremental export fingerprints message/conversation revisions, regenerates affected pages and reply summaries, reuses unaffected resources and switches its manifest last. A fresh export destination still requires writing the selected contents. State the search-performance differences between a static package and the native database reader.

Present protection and export choices together before starting. Keep source-database credentials distinct from archive passwords. Show phase, scanned/reused/changed/written amounts, message counts, bytes, rate, phase ETA, actual workers and fallback reasons. Do not fabricate an overall ETA when later work is unknown.

## Delivery and performance gates

1. Repair legacy index identity, statement reuse, batched checkpoints and full-scan diagnostics first.
2. Prototype v2 with large synthetic fixtures; compare persistent indexes and immutable pack I/O to the existing design.
3. Integrate source backups, resource references, native reading/search, then protection and reading exports.
4. Provide one-time, resumable legacy import. Preserve old archives; validate before the user selects the new default. A first migration or explicit rebuild may read all old data, normal updates should not.

Required cases:

| Case | Acceptance behavior |
| --- | --- |
| No-change large archive | No full historical-message scan, complete media copy or FTS rebuild |
| Small addition | Archive work grows with the delta; source fallback scans are separately identified |
| Historical edit/withdrawal/reaction | No timestamp-only misses; retain prior revisions |
| Opening/reply navigation | Range/ID lookups without loading one complete transcript |
| Chinese global search | Short/mixed-text cases and private-domain access controls |
| Resuming | Reuse committed index/cursors instead of restarting at old message 1 |
| Moving/copying | Valid logical archive/commit/index identities survive file-ID changes |
| Password change | Key wrapping is distinct from master-key rotation |
| Partial encryption | Migrate affected content objects/packs; separately report necessary public-index reconstruction and history/cache exposure |
| Crash/removal/full disk | Recover a complete commit; do not expose partial or unauthenticated records |

Benchmark million- and ten-million-message fixtures, many small attachments, large videos, NTFS/exFAT, low memory and concurrent reading. Measure cold/warm total time, first-page/query p50/p95 latency, CPU, peak memory, actual I/O, write amplification and recovery cost. Small-fixture speedups are not promises for real archives.

Gate work amplification before choosing wall-clock targets: with 100 additions held constant, growing history from one million to ten million must not introduce an archive-wide pass. No-change updates should read zero old combined-transcript bytes, copy zero old media bytes and perform zero FTS rebuilds. Report slow-source scanning separately. Queues, database caches and parse batches share a bounded memory budget rather than hiding history-proportional memory growth.

Freeze pack/chunk/cache/batch sizes only after prototype evidence. The format must demonstrate that ordinary incremental updates do not repeatedly pay costs linear in the entire archive while preserving verification and explicit rebuild paths.
