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

Existing reading copies are not rewritten automatically. The desktop preview supplies its current interface and missing translated UI labels without modifying archive files. A presentation refresh can replace the reader and index, retaining rollback copies while leaving JSONL, conversation pages, references and assets unchanged. Optional contact metadata resolves existing identifiers without re-exporting messages. New parsing that changes stored message parts still requires re-exporting a verified raw backup.

Version 0.6.9 reads NTQQ profile and group metadata, including remarks and group-specific cards. Consecutive text and mention segments render inline while explicit newlines remain intact. Missing senders, timestamps and empty records have explicit labels instead of invented epoch dates or invisible bubbles. Expanded QQ source includes a bounded protobuf field preview and the complete original Base64 payload; unknown fields are not assigned guessed meanings. Message search also matches resolved sender names and displayed text segments.

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

QQ palettes use borderless bubbles and circular avatars. WeChat palettes use compact tailed bubbles with quoted previews below the message; QQ keeps the quoted preview inside its bubble. Quotes still support navigation when target metadata is available. Message and image widths follow their content, independently of expanded source disclosures. Conversation rows are compact and show full names on hover.

Forwarding cards initially render five parsed entries. An expand/collapse control exposes the remaining parsed entries lazily, including their local media. Parsing safety limits still apply, and original payloads remain available in the source disclosure.

Recognized WeChat group sender prefixes are treated as sender metadata rather than message text. New exports preserve the original Text and store a separate DisplayText where needed, resolving the sender through available contact metadata. Existing reading copies also hide recognized prefixes in the desktop preview without rewriting their files.
