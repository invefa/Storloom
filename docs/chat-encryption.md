# Password-protected reading copies

In Chat Archives, choose **Encrypt / resume reader encryption**, select a completed reading-copy directory, and set a separate password of at least eight characters. The confirmation identifies the directory and explains plaintext removal. No password or encryption key is saved. There is no password recovery.

Choose whether to include media. With media disabled, message pages, source JSONL, names, indexes and reports are protected first; images, voices, videos and files remain readable on disk. The title and completion message distinguish partial protection from completion. Original raw backups and other copies are separate and remain unchanged.

The operation writes authenticated ciphertext, decrypts it to verify its SHA-256, durably records completion, and only then removes that file's plaintext. It refuses changed or shared sources. Use the task panel's Pause / Resume button to wait without discarding the current operation. Cancel or application interruption retains completed files and written chunks. Select the same directory again, enter the same password and resume; committed files are skipped and existing complete chunks are authenticated and reused. One to eight workers are available. More workers can hurt performance on a spinning disk.

Open `vault.storloom.json` using **Open reading copy**. Storloom requests the password and supplies decrypted, seekable streams to an InPrivate reader with `Cache-Control: no-store`; it does not extract a plaintext directory. Image preview, voice/video seeking, search and source details use the same reader. Explicit Save / Copy actions still produce plaintext where you choose. Close the reader before resuming encryption. Keep the marker and entire `.vault` directory together.

Format version 1 uses PBKDF2-HMAC-SHA256 (600,000 iterations, a random 32-byte salt) and AES-256-GCM with 16-byte tags. Files use 1 MiB independently authenticated chunks, random per-file derivation salts and per-chunk nonces. Associated data binds the logical path, header and chunk position. The plan and append-only completion journal are also encrypted and authenticated. See [Microsoft's AES-GCM documentation](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.encrypt?view=net-10.0).

Important limits:

- Encryption protects the selected reading copy, not raw backups, other copies, previously created browser caches, files you saved separately, screenshots or operating-system memory.
- Ordinary deletion cannot promise secure erasure of prior plaintext on SSDs, backups or filesystem snapshots. Full-disk encryption is complementary protection.
- Unfinished/unselected files remain plaintext. Files added after the encryption plan are outside that plan.
- Direct in-place updates remain unsupported. Use Update archive on the account archive page: it creates an EFS working generation, applies the saved protection rules and switches the catalog only after verification. The prior reader is retained. See the account archive section of Quick start for workspace access and storage limits.
- This is a new file format with automated corruption/recovery tests; it has not undergone an independent security audit. A damaged or lost vault, or a lost password, can prevent recovery. Keep an appropriate independent backup.

Pause / Resume is also available during raw backups, incremental backups and exports. It is cooperative: workers finish their current bounded operation before waiting. Cancellation remains available while paused. Pausing a backup retains its running process; it does not add restart-resume support to a raw-backup transfer. Interrupted reading exports retain their existing export checkpoint workflow.

Protection may also be selected in export settings. Fine-grained scope combines conversations, message types, content, sender or message IDs. Whole matching pages plus shared originals are recommended for speed and reduced quote-leak risk. Strict mode publishes sanitized public pages with selected messages removed and quote previews withheld, while unlock serves the full encrypted originals. It takes longer initially. Shared source records, indexes, reports and rollback copies are always protected; selected shared media affects every reference. A scoped operation does not protect the whole archive; unselected data may remain plaintext. Resume uses the saved scope.
