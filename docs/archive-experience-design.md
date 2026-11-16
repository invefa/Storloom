# Archive experience design

An archive should remain identifiable after moving machines, changing directories or losing application configuration. Portable metadata describes identity, origin and history; local configuration holds current registrations and preferences. Accounts, archive copies and original client sources are separate concepts.

## Implemented flows

| Situation | Behavior |
| --- | --- |
| Configuration loss or a new machine | Connect `archive.metadata`, or use **Find archived data** to scan a backup root and review selections before registering |
| Deep backup directories | Stop descending when an archive is found; do not enumerate message packs as separate archives |
| A moved archive | Resolve relative data paths; reconnect replaces a matching registration only when the previous location is missing. Managed projects additionally require matching original project identity, client and account |
| Two valid copies | Keep both locations; never silently replace a valid copy or remove its files |
| Multiple archives for one account | Group by client/account; unknown identities remain independent until associated manually |
| Provenance and conversion history | Preserve the original origin and inherit export/copy/import/repack history; display it in material history |
| Incomplete legacy history | Label recovered registrations and unknown original exporter versions explicitly; recovery time is not the original export time |
| Format compatibility | Separate metadata schema, export protocol, storage format and exporter versions; reject unsupported protocols without rewriting; read the legacy metadata filename |
| Password-protected data | Registration reads public metadata without unlocking; reading still requires the original password |
| Damaged, incomplete or unavailable archives | Show skipped paths and reasons, identify partial scans at resource limits, and retain successful registrations if another item fails |
| Library organization | Removing registrations retains disk data; the context menu opens the actual archive location |

`archive.metadata` is JSON beside `index.html`. Data paths are restricted to the current directory or `archive-data`. Public provenance paths are descriptive, never authorization for restore, update or deletion. Metadata schema is currently 2 and export protocol 1. Protocol versions change for export compatibility, independently of application patch versions. Existing compatible exports map to protocol 1 without guessing their original application version. History events deduplicate interrupted retries. Editable metadata is provenance, not a signed audit trail.

Discovery reads markers, does not parse message packs or obtain keys, refuses links, and bounds directory count/depth. Only confirmed selections change registrations. Directory names do not establish account identity. Native backups continue using their separate verified import flow.

## P1 features implemented in 2.15

- **Configuration packages:** the archive tools menu backs up selected registrations and preferences. Keys are excluded by default. Including keys copies protected key envelopes only and requires package encryption; their own passwords remain unchanged. Restore previews scope and resulting source locations, retains matching registrations by default, optionally updates them, saves recovery copies before writes, and rejects stale previews. Executable paths, custom-theme files, cleanup rules, running jobs and temporary credentials are excluded.
- **Version comparison:** choose two archives from the material or history view. Compare indexed message/resource counts, bytes, conversation changes, time ranges and recorded unresolved-media/unsupported-message counts. Existing indexes are reused; missing indexes may need cancellable rebuilding. Unrecorded folder-reader fields stay unknown. Equal totals do not prove identical messages.
- **Integrity reports:** compact archives verify committed records, including history, and resource authentication; protected folder readers verify registered files; plain folders without trusted digests report readability only; original folder backups use existing transfer-receipt verification. Results distinguish verified, partial, failed, locked and cancelled, with scope/time/snapshot and JSON export. Snapshot changes mark previous results stale. A previous successful check does not continuously verify current files.

Configuration packages are separate from chat data. Reports live in local application data and can be explicitly exported. They are not provenance signatures and do not modify archive contents. Export-time missing media or unsupported messages remain visible limitations even when stored ciphertext authenticates.

## Future priorities

| Priority | Gap | Direction |
| --- | --- | --- |
| P2 | Bulk original-backup discovery | Typed recovery results using existing manifest verification and unlock flows, never register originals as readers |
| P2 | Multiple archive operations | A queue preserving per-item configuration, progress and errors with resume/cancel controls |
| P2 | Stronger provenance trust | Optional signed manifests and offline verification, clearly distinct from public editable labels |

The P1 packages, indexed comparisons and integrity reports are implemented. Per-message text differences, bulk original import, a unified queue and signatures remain future work. See the [Chinese design](archive-experience-design.zh-CN.md) and [archive library](archive-library.md) for current operation details.
