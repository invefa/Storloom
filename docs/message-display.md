# Message display coverage

The display model is derived data. Full message text and available original binary payloads remain in JSONL and in a collapsed source disclosure in the reading copy. Original XML/JSON/protobuf is never executed as HTML or scripting. Large source DOM nodes are created only when expanded, and the reading copy no longer truncates text at 20,000 characters.

| Family | WeChat | NTQQ |
|---|---|---|
| Text and replies | Text, app type 57 with quoted sender/text | Ordered text and reply segments, available quote metadata |
| Media | Image, video, voice, animated stickers | Image, video, voice, face/sticker segments |
| File | Attachment card with available name | File card with available filename |
| Payments | Transfer amount/memo, red packet description | Typed wallet/red packet placeholder; no invented status/amount |
| Cards | Link, mini program, music, channels/live streams | Recognized structured JSON cards; otherwise a typed card placeholder |
| Other | Contact, location, forwarding, system notices, calls | System notices, calls, forwarding, gifts and interactive/formatted segments |

Only explicit fields are interpreted. Payment messages are historical records, not a statement of current settlement status. Contact names fall back to identifiers. Unknown element types retain a placeholder and source. Missing assets and recognized-but-unsupported local formats have separate placeholders. Matching is local-only and unambiguous; remote CDN assets are not downloaded. Plain brackets/emoticon names remain readable; no proprietary sticker pack is bundled.

Legacy WeChat `CompressContent` can contain LZ4 blocks; the decoder is bounded to 16 MiB and preserves its original bytes. Modern bodies retain Zstandard support. NTQQ uses the database's outer type and ordered `40800`/`45002` segments, with UTF-8 text interpreted only in known text fields. Protobuf parsing has field, length and recursion limits.

Encrypted WeChat V1/V2 image resources requiring another key, resource-index-only references, SILK voice, unknown schemas, unsupported structured fields and some quoted/forwarded submessages still need future adapters. A card/placeholder does not mean that every field has been parsed. `unsupportedMessages` and `unresolvedMedia` prevent a fully readable result from being falsely claimed. Raw backup verification and source preservation remain unchanged.

Existing reading copies are not rewritten automatically. Re-export a verified raw backup for new parsing; source clients and originals are not required when the backup and necessary key are available.

## Research references

The parser is independently implemented in C#. Protocol notes are version-dependent evidence, not an official universal schema:

- [QQ database body fields](https://github.com/QQBackup/nt_msg_db_util/blob/master/db_docs/c2c_msg_table/40800.md) and [shared schema](https://github.com/QQBackup/nt_msg_db_util/blob/master/msgdb/proto/c2c_40800.proto).
- [QQ outer and element type reference](https://github.com/shuakami/qq-chat-exporter/issues/167). Runtime element enums and database field numbers are different namespaces.
- [WeChat message and application types](https://pkg.go.dev/github.com/ysy950803/chatlog/internal/model).
- [WeChat XML examples](https://github.com/lefex/iWeChat/blob/master/MESSAGE.md).
