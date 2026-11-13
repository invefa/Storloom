# Compact archive v3, application 2.7

New desktop accounts recommend compact storage for independent reading and native-backup outputs. Existing projects retain their format. Compatible-folder export and granular/direct-key protection remain available. See [user instructions](../docs/compact-archives.md).

## Storage and publication

- Commit descriptors retain their authenticated digest and parent-linked, HEAD-last publication. Existing v2 JSON payloads remain readable; new stores use binary `SLPACK03` payloads.
- A pack has a 16-byte header, protected/compressed metadata with object ranges, and independently encoded binary resource records. Resources are not serialized as Base64. Fast Brotli quality 1 is used only when beneficial; sampled high-entropy data bypasses compression. Packs have a 64 MiB upper bound, with resource batches normally flushing around 8 MiB. Messages flush at 4096 rows or roughly 2 MiB of serialized characters. Small-file metadata batches flush at 1024 entries.
- Resource chunks remain 1 MiB. Content IDs are SHA-256 in public archives and keyed HMAC in protected archives. Protected records use domain-separated keys and AES-256-GCM. Query databases remain encrypted using the existing native SQLite encryption integration. Password wrapping is separate from the random archive master key.
- Query replay verifies and decodes v3 metadata without scanning all resource bytes. Resource reads open only the indexed ranges, with a bounded four-pack directory cache and per-resource identity validation. Full verification still checks complete immutable pack digests and all resources.
- Compressed binary query rows replace repeated textual JSON in new projection writes. Existing textual rows remain readable. Search postings and normalized search text remain indexed independently. Base64 is only an internal transient adapter when returning BLOB query values through the existing string-oriented wrapper; it is not written as the query row or resource storage representation.

## Direct ingestion and updates

The existing database parsers, media decoders, identity history and unsupported-content reporting feed a compact writer directly. No full HTML reading tree or duplicated media folder is created first. Parsed-database spools and small conversion/avatar assets still use a task workspace. Protected reading ingestion retains the existing EFS workspace requirement.

Database hashes select changed databases. Those databases are reparsed; this is not universal row-level source capture. Source-database scope, native message keys and occurrence ordinals keep repeated events distinct. Stable indexed lookups skip previously complete unchanged messages before media conversion. Missing media retries remain possible. Historical display identities and old resources are retained.

An update uses a private `.write-head` and query projection. Checkpoints bind to source identity and fingerprints. Public HEAD stays unchanged until the private data and projection are complete. A reader captures one published head, owns a query-cache lease and remains on that snapshot while updates publish. A projection lock coordinates opening and reclamation. Unused derived stage indexes are reclaimed only under an exclusive cache lease and a strict filename allowlist; packs and manifests are excluded.

Preparing a query snapshot currently copies the index sequentially. This avoids rebuilding message/search indexes or copying historical media, but is still proportional to index size. It is cancellable, reports bytes and needs temporary index space. This cost must remain visible when assessing large-archive incremental performance.

## Native snapshots and maintenance

Native backup plans file metadata, reuses unchanged file versions and deduplicates chunks. Snapshot manifests have up to 256 hash buckets. New snapshots reuse unchanged manifest pages; only changed buckets and small root manifests are added. Historical versions and empty directories remain restorable. Restoration checks paths, boundaries, available space, duplicate paths, resource identities and complete output hashes. It writes a new directory and never merges into client storage.

Reading from a compact native backup currently materializes a native input snapshot for existing parsers. This optional path needs full temporary native-copy space and verifies restored files. Direct local reading avoids that copy.

Explicit reorganization streams current messages and retained native snapshots into a separate destination, re-encodes protection as selected, and excludes unreferenced data and message revision history. Destination checkpoints support continuation. The source remains intact. Password-only changes rewrite the key envelope without reorganization. Statistics distinguish packs, logical files, query caches and other metadata; they do not claim filesystem allocation measurements.

## Validation and observations

Release fixtures cover v2 compatibility, direct public/protected ingestion, unchanged and changed databases, repeated occurrences across databases, pending/private publication, old-reader snapshot stability, inactive-cache reclamation, source mismatch/abandonment, metadata-only projection rebuild, individual resource corruption, native snapshot history, restoration, reorganization, password changes and portable HTML export. Desktop fixtures cover compact format controls, worker spacing, export/restore visibility, themes and narrow windows.

Single-run Release measurements on one local disk, using synthetic data and warm OS caches:

| Operation | Public | Protected |
| --- | ---: | ---: |
| Write 20,000 messages and 1,000 tiny resources | 3.30 s | 2.54 s |
| Compare 100 existing messages and append 100 | 0.135 s | 0.149 s |
| Reopen, unlock if needed, read first 300 rows | 14.7 ms | 84.6 ms |
| One selective substring query | 1.43 ms | 0.93 ms |
| Password change | n/a | 56.6 ms |
| Data packs after the incremental write | 6 | 6 |
| Pack file lengths | 1.17 MB | 1.28 MB |
| Query-cache file lengths | 20.09 MB | 20.37 MB |

Initial creation/unlock KDF time is separate from the write measurement. Query caches include the current projection and the small initial projection; inactive stage copies were reclaimed. Search and snapshot copying are included in the incremental measurement. These are component measurements: source database discovery, hashing, parsing, media decoding, HTML export, cold-cache I/O and filesystem allocation overhead are excluded.

A separate 32 MiB mixed-entropy fixture exercised 1-8 pack workers. Single-run totals ranged from 323 to 380 ms, with small differences and substantial fixed overhead. This does not establish an optimum. Existing automatic selection considers storage seek penalty, CPU count and memory, with a manual 1-8 override. No new two-physical-disk, million-row end-to-end, p50/p95, exFAT or low-memory guarantee is made by these fixtures.

Windows index connections use `multipleciphers-win32-longpath` with extended native paths, including journal paths. A bare `win32-longpath` VFS bypasses the encryption shim and is rejected. See the [maintainer VFS guidance](https://utelle.github.io/SQLite3MultipleCiphers/docs/faq/faq_overview/). Public and protected regression fixtures create index paths longer than 260 characters.
