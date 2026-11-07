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

Supported V1/V2 image containers now decode offline when a validated key is available. WxAM/HEVC images, resource-index-only references, SILK voice, unknown schemas and unsupported structured fields still need future adapters. A card/placeholder does not mean that every field has been parsed. `unsupportedMessages` and `unresolvedMedia` prevent a fully readable result from being falsely claimed. Raw backup verification and source preservation remain unchanged.

Existing reading copies are not rewritten automatically. The desktop preview supplies its current interface and missing translated UI labels without modifying archive files. Re-export a verified raw backup for new parsing or an updated standalone HTML reader; source clients and originals are not required when the backup and necessary key are available.

## Research references

The parser is independently implemented in C#. Protocol notes are version-dependent evidence, not an official universal schema:

- [QQ database body fields](https://github.com/QQBackup/nt_msg_db_util/blob/master/db_docs/c2c_msg_table/40800.md) and [shared schema](https://github.com/QQBackup/nt_msg_db_util/blob/master/msgdb/proto/c2c_40800.proto).
- [QQ outer and element type reference](https://github.com/shuakami/qq-chat-exporter/issues/167). Runtime element enums and database field numbers are different namespaces.
- [WeChat message and application types](https://pkg.go.dev/github.com/ysy950803/chatlog/internal/model).
- [WeChat XML examples](https://github.com/lefex/iWeChat/blob/master/MESSAGE.md).

## References, forwards and notices

A small source disclosure sits below each message; expanded raw content has its own auxiliary bubble. System notices, invitations and poke notifications use a centered notice layout without a fabricated sender. WeChat notice templates substitute available participant names. Embedded WeChat forwarding records and NTQQ cached forward snapshots show their included messages, names and timestamps; cards containing only a server resource token retain an explicit unavailable-content placeholder. Forward depth and item counts are bounded.

Quoted replies can jump to their original message within the same conversation, including another page. WeChat server IDs and NTQQ group sequence links are indexed as strings to retain 64-bit precision. Candidate direct-message reply IDs are used only when an exact target exists in that conversation. Missing target messages are reported rather than guessed. Full media is preferred; a thumbnail fallback is labelled and prevents a false complete result.

- [WeChat media formats and offline image-key derivation](https://github.com/qiaodogbear/ChatTrace/blob/f9fcdb42e8fb99a527c0bcd7d6ff7ea4bc891563/src/chattrace/service/image_key.py).
- [NTQQ group reply links](https://github.com/QQBackup/nt_msg_db_util/blob/master/db_docs/group_msg_table/summary.md) and [cached forwarding fields](https://github.com/QQBackup/nt_msg_db_util/blob/master/msgdb/proto/group_40900.proto).

The reader offers Default, WeChat and QQ-inspired palettes with independent light/dark switching. The toolbar remembers the selected palette in local browser storage when available. No transcript is stored by that preference. The default light scheme remains grayscale. Message bubbles use 6 px vertical and 10 px horizontal padding; source bubbles and quote blocks are also compact.
