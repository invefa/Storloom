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

## Future priorities

| Priority | Gap | Direction |
| --- | --- | --- |
| P1 | Full local configuration still needs its own backup | Explicit configuration packages separating registrations, preferences and keys; exclude secrets by default, encrypt and confirm when included |
| P1 | Comparing valid archive versions | Reuse indexes to compare message/resource counts, time ranges and missing media without decrypting everything to render a list |
| P1 | Unified integrity status | Surface existing verification with scope, time and outcome; readable metadata must not imply a fully verified payload |
| P2 | Bulk original-backup discovery | Typed recovery results using existing manifest verification and unlock flows, never register originals as readers |
| P2 | Multiple archive operations | A queue preserving per-item configuration, progress and errors with resume/cancel controls |
| P2 | Stronger provenance trust | Optional signed manifests and offline verification, clearly distinct from public editable labels |

These future priorities are design proposals, not implemented features. See the [Chinese design](archive-experience-design.zh-CN.md) for the full scenario discussion and [archive library](archive-library.md) for current operation details.
