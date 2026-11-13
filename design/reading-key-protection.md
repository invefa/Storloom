# Reading copy keys

Version 2.6 makes password-protected key envelopes the default for ordinary encrypted reading copies. Packed archives already used password-wrapped random master keys. Both formats keep password changes separate from content-key rotation.

A new ordinary reader has a random 256-bit data key. PBKDF2-HMAC-SHA256 with a fresh 32-byte salt and 600,000 iterations derives a wrapping key from the password. The existing AES-256-GCM envelope primitive authenticates the data key and a unique key-envelope purpose. Neither account identifiers nor device information derive the data key. Existing chunk and metadata ciphertext formats remain unchanged.

The version 3 marker records either a password envelope or direct-key mode. Direct mode accepts exactly 64 hexadecimal characters representing a 256-bit key and stores no wrapped data key. Changing that key requires rewriting content. Key-mode choices are exposed at reading creation and standalone protection; existing direct-key readers keep their mode on updates.

Password changes acquire the existing writer lease, reject stale or shared markers, and atomically replace only the marker. Versions 1 and 2 remain readable; changing a legacy reader password wraps its authenticated original data key without rewriting its plan, commits, media or transcript. The legacy partial-rule requirement is preserved. Existing unlocked sessions, historical generations and externally retained copies keep their previous access. Rewrapping does not revoke a compromised data key. Managed tasks must finish before changing their current reader password.

Incremental generations retain the authenticated data key so unchanged encrypted resources remain reusable. New envelopes use the supplied current password. Paused encryption resumes using the newly wrapped key. Creating or rewrapping version 3 readers requires 2.6 or later to open them; unmodified older readers remain readable.

## Space use

Ordinary readers still keep individual media and page files. Ordinary vaults likewise use an encrypted file for each planned file, plus encrypted metadata and journals. Encryption adds a 52-byte file header and 28 bytes per content chunk; allocation rounding and filesystem metadata add separate overhead. Reader generations physically copy retained media, including reused ciphertext. Retained generations can therefore dominate space use even without adding many messages.

Raw incremental backups reference unchanged non-database resources by manifest instead of copying them again. Database and WAL snapshots remain physical per-generation copies to preserve restore compatibility. A first native backup still retains its many original files.

Packed archives aggregate content into bounded delta packs, deduplicate repeated resource chunks and keep a rebuildable search cache. This reduces per-file allocation overhead, but indexes, retained revisions, serialization overhead and source readers kept alongside the pack still consume space. Packing is not an automatic promise that total archive bytes will be smaller than original content. This release changes key custody, not the ordinary-reader or native-backup storage layout.
