# Storloom

A Windows storage manager for understanding disk usage, reviewing cleanup rules, verifying transfers, and keeping local chat archives. Written in C# with a native WinForms interface; no Python runtime or cloud service is required.

[简体中文](README.zh-CN.md) · [Rule reference](docs/rules.md) · [AI rule guide](docs/ai-rules.md) · [Chat compatibility and research](docs/chat-exporters.md)

## What it does

- **Storage overview:** scan a mounted drive or folder, drill into directory sizes, inspect the 100 largest files, and review skipped paths.
- **Rules and cleanup:** preview file matches, filter and uncheck individual candidates, save/load plans, and explicitly execute Delete, Copy, or Move rules. Includes 23 cache/log/package rules.
- **Copy and move:** preview a new destination, copy all files and empty directories, verify SHA-256, and retain a manifest beside the archive. Move removes originals only after the complete destination is verified and each source/target pair is rechecked.
- **Chat archives:** discover QQ/WeChat folders or select one manually; make a verified raw backup including media; export supported message databases to offline HTML and JSONL. Keys remain in memory. Export reports distinguish complete, incomplete, and cancelled work.
- **Languages:** English and Simplified Chinese, selectable at runtime. Rule authors can supply translated names and descriptions. Low-level diagnostics and the CLI command interface use English or OS messages.

## Run

On 64-bit Windows 10/11, extract the Windows x64 package into a writable directory and run **Storloom.exe**. The package includes the .NET runtime. `cli/Storloom.Cli.exe --help` provides the command interface.

1. Scan a folder in **Storage overview**. Sizes represent logical file lengths, not guaranteed reclaimable physical space; sparse, compressed, and hard-linked files can differ.
2. In **Rules and cleanup**, scan and inspect both candidates and diagnostics. Review-risk rules start unchecked. Deletion bypasses the Recycle Bin.
3. In **Copy and move**, use **Copy and verify** for the first backup. Choose a destination that does not exist; existing directories are never merged or overwritten.
4. In **Chat archives**, choose a backup destination, discover/select the account, obtain a key if needed while logged in, then close the client and back up the raw account folder. Export the verified snapshot. Read the report and retain the raw backup even when readable export succeeds.

A backup copy alone does not release source space. A readable chat export does not authorize deleting the account folder. For a later deliberate move use the transfer page; clients may recreate their original folder, and Storloom does not create junctions or rewrite client storage settings. Readable export covers supported database records, not guaranteed playback or rendering of every media/message type.

## Boundaries and local data

Analysis is read-only. Rules never run shell commands. Cleanup protects system/application components, credentials, broad roots, and user document folders; locked applications, reparse points, changed file identities, hard links, and expired plans are refused or skipped. Default cleanup settings allow ready fixed drives, while built-in rules target known cache paths. Restrict `allowedDrives` and `protectedRoots` before using custom delete rules on backup disks.

Transfers reject overlapping roots, links, existing destinations, and protected move sources. They preserve file contents, last-write times, and empty directories, not NTFS ACLs, alternate streams, or a bootable system image. Interrupted staging directories and journals are retained for inspection; there is no automatic resume or merge. Empty source directories remain after moves. A manifest verifies bytes, not future client restorability; restore to a separate directory and test with the appropriate client version.

Paths must be canonical absolute Windows drive paths. UNC paths are unsupported; a mapped drive can be selected explicitly for analysis/transfers. Windows system SQLite is used for read-only message queries. No elevation, scheduled cleanup, telemetry, or external AI API is configured by the application.

Preferences, custom rules/settings, scans, plans, and journals are created under executable-adjacent `data/`. They can contain private paths and account information. Backups and readable exports are also private, and readable messages are plaintext. These files are excluded from Git and release packaging. Release archives contain no user's settings or data. The optional signed-client login capture is version-dependent; see the compatibility document before using it.

## AI-authored rules

Give an AI the [rule guide](docs/ai-rules.md) and [JSON Schema](rules/rules.schema.json), describe the folders and desired operation, then import its JSON. Import only validates data; **scan, inspect the exact paths/actions/destinations, and confirm** before execution. Rules contain no executable code. AI integration is a local import workflow, not an embedded chat service.

## Build and package

Install a .NET 10 SDK on Windows. The pinned baseline rolls forward within the .NET 10 feature bands.

An optional isolated SDK can live in ignored `artifacts/toolchain/dotnet/`. `global.json` checks this relative directory first, then the SDKs beside the invoking host (`$host$`). This discovery feature needs a .NET 10 or newer host. VS Code and ordinary `dotnet` commands can then use the same SDK without machine-specific paths in the repository. If no isolated SDK is present, a regular .NET 10 SDK installation works as usual.

```powershell
dotnet build Storloom.sln -c Release
dotnet test Storloom.sln -c Release
./scripts/Publish.ps1 -Version 0.2.0
```

Packaging publishes self-contained x64 binaries into a fresh ignored directory, copies an allowlist of public documentation/rules/licenses, and writes a ZIP plus SHA-256. No local configuration is copied. The Windows CI workflow builds/tests on pushes and PRs; the release workflow builds an artifact on manual dispatch or a version tag. It does not publish a GitHub release automatically.

Tests cover cleanup boundaries/identity, copy/move verification and cancellation, encrypted pages and committed WAL frames, synthetic QQ/WeChat exports, HTML escaping, duplicate preservation, locale parity, and rendering all four pages in both languages. Real client versions, login capture, media playback, and account restorability are **not certified by fixture tests**.

## License

MIT. The adapted WeChat native capture component and ZstdSharp dependency retain their own notices in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). .NET runtime notices accompany self-contained packages. QQ/WeChat names belong to their respective owners; this project is independent.
