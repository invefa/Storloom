# Chat backups and reading copies

Storloom creates local backups and a separate reading copy. A reading copy preserves available original message payloads, but cannot recover content or attachments that are absent from the source. See the export report for unsupported messages and unresolved resources. Keep the verified raw backup and its manifest.

## Backup and export

1. Select the complete account directory, including databases and media.
2. Choose a direct reading export or a verified raw backup independently. Close the source client before reading its account data. No database key is required for raw copying.
3. For a direct account export or an export from an existing backup, in **Database access**, obtain a matching key, type a retained key, or load a key file. Captured and imported keys must authenticate the selected database. Key acquisition is explicit and version-dependent; signature checks and timeouts remain enforced.
4. In **Reading copy options**, choose verification and optional password protection. The reading password is separate from the database key. Protection starts only after export finalization; an unfinished export is still plaintext.
5. Open the result in the built-in reader. Inspect incomplete reports before relying on it.

Database keys are not automatically saved or logged. **View / retain verified key** reveals, copies or exports a key only on request. Saving creates a password-protected key file; store it separately and securely. A key can stop matching after a client/database change. Key files and the clipboard are outside automatic key cleanup.

## Continue without starting over

**Resume reading export** continues an interrupted export of the same immutable backup. Keep its progress file and work directory together. Finished databases and committed pages are reused; an unfinished database may restart. Re-enter or obtain its matching key.

**Incremental backup** creates a new generation, copying changed files while reusing verified unchanged resources. Databases and their transaction files are copied together. Keep all referenced raw generations and manifests; a newer generation may depend on earlier files.

**Update reading copy** merges a newer verified backup into a completed ordinary reading copy of the same account. It preserves existing history, reuses resources and rebuilds affected pages. The first update may need a one-time history scan. Updates still parse changed databases and need working space. Close the reading window before updating. Managed protected reading copies are updated through an encrypted workspace and published as a new protected generation.

## Progress and performance

Backup, export and encryption support cooperative pause/resume during the current application session. Cancel preserves completed export checkpoints or encrypted chunks; raw backup pause does not add restart recovery to a transfer. Use fewer workers on a hard disk and compare throughput before increasing parallelism. More threads can increase disk contention.

Debug output describes stages and relevant work counts. Turning it off affects future diagnostic events and retains history. It never intentionally includes keys. Preparation of large encryption plans reports file inventory, matching, key derivation and plan-saving stages.

## Compatibility and privacy

Database, media and resource schemas vary by client version. Supported formats use authenticated decryption; unexpected schemas or authentication failures fail explicitly. Only unambiguous local resources are associated. Installed resource extraction is opt-in outside the desktop flow and never executes client libraries. No proprietary emoji assets are included in the package or source tree.

Original binary/XML/JSON payloads remain data, never executable viewer content. The reader is local, restricts navigation and remote loading, and exposes bounded save/copy actions. Explicit optional public avatar downloads are separately labelled in settings. Source backups are never deleted by export. Read [message display](message-display.md), [password protection](chat-encryption.md), and the repository's third-party notices for provenance.

## Account discovery and independent outputs

The account picker searches common locations and redirected Documents folders automatically. Searches skip media trees, use a bounded depth, and omit full media size scans. Account and client locations can be configured from the picker or Tools > Preferences. Add search roots, disable automatic discovery, or override a client executable. Process paths and installation registry entries provide automatic executable hints; manual browsing remains available.

After selecting an account, Export reading copy reads the closed client's account directly without requiring a raw backup. Raw backup remains a separate action; exports from existing backups and managed updates remain available. Direct exports support in-session pause. Cross-restart export resume still requires an immutable backup source. A reading copy is not a restorable client backup.

## Password-protected database key library

Explicitly saved database key files use independent passwords, AES-256-GCM, random salt and nonce, and PBKDF2-SHA256 with 600000 iterations. Minimum password length is 8 characters. Any file extension is accepted. The library catalogs file locations and account labels only. Passwords are not saved. Imported keys must authenticate the selected database before use. Legacy plaintext key files remain explicitly importable. Initial acquisition may still require login or debugging; a saved matching key avoids repeated acquisition.

## Restore client data

More actions > Restore client data accepts a completed raw backup manifest and a new destination. It materializes inherited incremental resources, verifies SHA-256, and publishes a self-contained account directory only after completion. Existing directories are never merged or overwritten. In-session pause is supported. A failed or cancelled restore leaves a clearly marked partial directory, not a completed destination.

Restoring to a separate directory does not modify a running client. Close the client before adopting restored data, select the restored data location in a compatible client version, and sign in normally. The tool does not replace active client directories, transfer login credentials, or guarantee cross-version database compatibility.
