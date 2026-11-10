# Message display and navigation

Reading copies show ordered text, inline mentions and built-in emoji; standalone images and animations use bare media layouts. Mixed text and media retain a bubble. Voice messages use compact players. Alternate image representations can be switched or displayed together next to the source disclosure.

Supported records include files, replies, forwarding, links, mini applications, contacts, locations, calls, payments, gifts, system notices, recalls and poke notifications. Unsupported fields retain an explicit placeholder and original payload. Historical payment cards do not assert a current settlement state.

Available emoji reactions appear as compact emoji/count badges below the corresponding message. A recall is a distinct system notice. Missing reaction fields cannot be reconstructed from a screenshot or inferred from a recall event. Expired or absent attachments remain unavailable.

## Navigation

Use first/last page, an exact page number, or top/bottom of the current page. Set 100–2,000 messages per displayed page; larger pages cost memory and rendering time. This only changes presentation and does not rewrite archive data.

Search by content, sender, type, dates or your own messages. Choose the current conversation or the entire open archive; the global shortcut is Ctrl+Shift+F. Search results retain a return point when opening their message context. Previous/next matching-page controls move through conversation results; archive results use bounded batches.

Click a quote to locate its exact archived target. Missing targets produce a brief notice. Located messages are highlighted. Click an avatar/name for available identity data and observed name/avatar history. Click the conversation title for its details. Archived senders are observations, not a complete current membership list.

The optional navigation minimap uses lightweight marks for the mounted page. Collapse keeps its content; close releases its canvas and observers. It does not load extra history or media. Sidebar width and image-preview size are remembered locally.

## Media and original content

Image preview supports resize, drag, wheel zoom, rotation, copy, save and locating the sending message. Avatar previews use the same viewer. Browser-only copy support depends on browser permissions. Explicit copy/save creates plaintext.

Original payloads are collapsed, created lazily when expanded and displayed as text. Their width is independent from the message bubble. Known binary records have a bounded decoded field preview; unknown fields are never assigned guessed meanings. Newer display code can be applied to ordinary reading copies without modifying their transcript; recovering missing resources or newly parsed data may require the matching raw backup and key.

## Coverage limits

Client versions and resource layouts vary. Local image decryption, animation caches, voice databases and installed emoji tables are supported only where their layout is recognized and authenticated. A thumbnail fallback is labelled. Missing media and unsupported messages remain separately counted. No reading copy replaces a verified raw backup, and no result guarantees recovery of absent or expired resources.
