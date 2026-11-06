# Chat archives: compatibility and implementation choices

Reviewed 2026-11-07 using upstream source and licenses. These are independent projects; their capabilities and licensing can change. Upstream support claims are not Storloom compatibility guarantees.

| Project | Upstream approach | Decision |
| --- | --- | --- |
| [chatlog-keeper](https://github.com/labazhou2024/chatlog-keeper), `2225294fd0761e10ce15540f21e7dbb2ac304c3c` | MIT; Python orchestrates WeChat/QQ decryption and export, including a C# Windows WeChat login-capture component | Adapt only the native C# component, retain MIT attribution, implement the local archive pipeline in C#; no Python runtime |
| [qq-chat-exporter](https://github.com/shuakami/qq-chat-exporter), `4ae4b700939346b46b05b46346fb5c5c5f08b081` | GPL-3.0; NTQQ/client integration, multiple export formats and media support | Strong QQ-specific option; not bundled or copied into this MIT application. Client/plugin integration has different deployment and maintenance requirements |
| [ChatTrace](https://github.com/qiaodogbear/ChatTrace), `f9fcdb42e8fb99a527c0bcd7d6ff7ea4bc891563` | MIT; Python/Frida WeChat 4.x tooling and media handling | Useful compatibility reference; no Frida dependency or Python backend bundled |
| [sjzar/chatlog](https://github.com/sjzar/chatlog) | Upstream README says code/history were removed on 2025-10-20 | Not used as a maintained engine or download dependency |

## Current implementation

Storloom uses .NET cryptography, Windows `winsqlite3.dll` for read-only SQLite queries, and ZstdSharp.Port for compressed message bodies. The adapted C# capture component is isolated under `Chats/NativeCapture`; its license is in the third-party notices. Runtime operation does not fetch upstream tools or execute downloaded scripts.

| Area | Implemented | Limits |
| --- | --- | --- |
| Raw archive | Whole selected account folder, databases/WAL/media included, SHA-256 verification, sidecar manifest | Close QQ/WeChat first; no client path rewrite, junction, or automatic removal |
| WeChat 4.x DB | 4096-byte pages, SHA-512 page authentication, raw 32-byte key or PBKDF2-SHA512 256000, reserve 80; Msg_* and Name2Id tables, Zstd bodies | Client-specific schemas/keys may change; conversation table hashes may remain as IDs |
| WeChat 3.x DB | PBKDF2-SHA1 64000, reserve 48; MSG table text/XML | No legacy key extraction guarantee; contact names and sender resolution are incomplete |
| NTQQ DB | nt_msg.db, wrapper 1024 or 0, PBKDF2 4000, authenticated SHA-1/SHA-512 compatibility combinations; direct/group message tables; text/attachment protobuf fields | Legacy pre-NTQQ formats and every protobuf element are not supported. Exact wrapped Windows zero lock-page exception is handled; other authentication failures refuse export |
| WAL | Header/frame checksums, salts, page authentication, last committed frame and database size | Uncommitted tails excluded; malformed committed frames fail. This is not a live-database snapshot service |
| Output | Streaming JSONL (raw binary payload in base64), escaped static offline HTML, completeness report | One chronological stream per table/database; not globally sorted across shards, no full contact directory, no guaranteed media playback |

`Complete` in the report means all discovered **supported message databases** were read without reported errors and at least one message was emitted. It does not certify semantic decoding of every message, coverage of undiscovered database families, or media rendering. Identical messages are retained rather than heuristically deduplicated. An unsupported schema/key/page produces a failure or incomplete result rather than a fabricated empty transcript.

## Keys and consistent snapshots

1. Choose an account directory you own. Automatic discovery checks known current-user locations; relocated data can be inspected manually. The size column reports database size, not total account/media size.
2. If required, use **Read key (passive)** while logged in. It reads only current-user QQ/WeChat process memory, has a bounded search, and accepts a candidate only after database HMAC authentication. It does not modify the process. This does not find every version's keys.
3. WeChat 4.x has a separate explicit **login capture** action. Close daily WeChat first, select installed Weixin.exe, and log in to the new child instance. Storloom validates locally trusted Tencent Authenticode signatures on the executable and Weixin.dll, checks a supported fingerprint, and uses the adapted native debugger component only for that child. It does not attach to an already-running client. A separate worker hosts the debugger and exits after capture; Windows then ends its newly created client tree. The native login wait is bounded to 90 seconds and the worker has a two-minute outer deadline. Cancellation ends that worker. This is an explicit, version-dependent operation; the daily client must already be closed. The raw snapshot exporter handles committed WAL data after the capture instance exits.
4. Alternatively paste a key into the masked field: WeChat hex bytes, NTQQ ASCII passphrase. The CLI reads a private `--key-file`, never a key in the process command line. Captured keys are never saved or logged; mutable byte buffers are zeroed after use. .NET strings, OS paging/crash dumps, and user-created key files are outside a promise of perfect memory erasure.
5. Close the source client, make a **raw backup**, then export the verified archive. A verified sidecar allows offline export even when the original client is later running. CLI: `chat-export --platform WeChat --backup MANIFEST.json --destination NEW_FOLDER --key-file PRIVATE_FILE`.

Exports are plaintext and should be protected like the original conversation. Private working decrypted databases are removed on normal completion, cancellation, and handled failures. An OS/process crash can leave `.work-*` files in the chosen export directory; inspect and remove them after confirming no export is running. Partial export reports remain explicitly incomplete. No chat content is uploaded.

## Validation status and space reclamation

Automated tests use synthetic encrypted QQ/WeChat pages, committed WAL frames, SQLite message databases, tampering, cancellation, duplicate messages, and HTML escaping. No personal account transcript is checked into source or tested by CI. Real-client login capture, every active QQ/WeChat release, large-account performance, contact/media decoding, and restore compatibility need version-specific manual validation.

Raw backup and readable export preserve originals. To reclaim space later, first verify the raw archive and retain the manifest, then deliberately plan a separate Move of the desired folder to a fresh destination or use client-supported storage management. Storloom does not infer that readable export makes chat data disposable. There is no one-click deletion of an account after export and no cross-version client restore guarantee.

中文：当前重点是“完整原始备份 + 明确标注覆盖范围的阅读导出”。不支持的格式会报告，不把部分导出伪装为完整恢复方案。备份后释放空间需要另行审核迁移；程序不会替你停止客户端、创建联接或删除聊天原件。
