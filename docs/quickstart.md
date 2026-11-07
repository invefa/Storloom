# Getting started

Storloom starts on **Cleanup and archive**. Nothing is cleaned on startup. Press F1 for contextual help; open Tools > Preferences to choose language, archive/backup destinations, and rule sets. File > Open local data opens private plans, custom rules, and records. Help > About shows version, developer, and the repository.

## Clean or archive caches

1. Open Preferences > Rule sets and enable only the software categories you need.
2. Click Scan cleanup items. Select an item on the left; inspect its files and diagnostics on the right.
3. Check the items to process. Uncheck individual files to retain them. Every item starts unchecked.
4. For a file item, choose its Treatment: the rule's action, Archive and keep originals, or Archive then remove originals. The archive destination is set in Preferences.
5. Click Review selected. The review shows exact actions, destinations, source samples, and any native commands. Confirm and execute is the mutation boundary.

Archive copies only the selected matches, not the whole source folder. Each rule/root gets a new directory. SHA-256 verification finishes before a move starts removing originals; both sides are checked again for each removal. Existing destinations are not merged. Copy does not reclaim source space. Permanent deletion bypasses the Recycle Bin.

## Use a tool's own cleanup

Enable Application cleanup commands. Supported recipes are NuGet HTTP cache, npm cache, pip cache, and uv prune. Install the relevant tool first or set its private executable path in `data/settings.json` under `toolPaths` (keys: dotnet, node, python, uv). The application and chat exporters require no Python; the optional pip recipe invokes your existing Python installation.

Scan previews the executable, fixed arguments, and executable SHA-256 without cleaning. Review the command before confirming. Native commands apply to the tool's whole configured cache, so per-file exclusions and age/size filters do not apply. Choose either a file rule or a native rule for the same cache; running both can make the later operation find no files. uv prune may also remove shared project environments, which can need rebuilding. There is no arbitrary shell/script execution. Timeout defaults to 300 seconds (`commandTimeoutSeconds`, 1..3600). Cancellation/timeout ends only the child process tree started for that command; tool changes already completed are not rolled back. Output and exit status remain in private records.

## Archive QQ or WeChat

1. Open Chats. Click Find accounts or Choose account folder. Select the complete account folder containing databases and media, select its row, then Next.
2. Set Backup location. Quit QQ/WeChat and click Back up original data. This copies the complete folder and verifies it; no decryption key is needed. Originals remain.
3. The reading-copy page appears after verification. For encrypted data, reopen the client and log in to the same account, then Read key > Read from logged-in client. It authenticates candidates against the archived database. The original client can stay open during offline export. Plain SQLite needs no key.
4. If passive reading fails on a supported WeChat 4.x client, Read key > Start WeChat login capture is an explicit alternative. Close daily WeChat first and select installed Weixin.exe. Log in to the new child. The isolated worker/client tree exits at capture/cancellation/timeout. Client signatures and supported code fingerprints are checked.
5. Click Export reading copy. Use Open reading copy and Open export report. Keep the raw backup, which contains media; HTML does not guarantee full message/media playback. Inspect incomplete results rather than treating them as complete backups.

Keep raw backup only finishes without decryption. Use previous verified backup lets you continue after restarting Storloom. File > Open chat backup opens the sidecar manifest for an archive even when its original source is no longer present. These workflows do not delete chat originals or rewrite client paths. Export keys stay in memory; process crashes can leave temporary decrypted `.work-*` files in the export directory. Account layouts and client versions can change; see [compatibility](chat-exporters.md).

## Relocated caches

Unknown junctions and symlinks remain blocked. If a cache was relocated, inspect its link target and add a private file rule for the verified physical cache leaf. Do not point a rule at a whole profile, Steam HTML profile, app settings, chat account, or application directory. For example, a Steam renderer cache is disposable while its neighboring Local Storage and Network folders can contain sessions. Local rules are merged by ID over enabled sets, and remain private.

## Progress and results

The operation area shows phases, throughput, estimated remaining time when a total is known, and a collapsible log. An unknown-size scan stays indeterminate. Copy work includes writes and verification reads, so its work-byte total exceeds source size. Cancellation preserves completed work and journals. Results list released logical bytes per cleanup item; native tools show complete before/after scans, with unknown values excluded from totals.

## Conversation reader

Use Open reading archive after export, or File > Open reading archive to select messages.html. Search conversations on the left, read sender-labelled bubbles on the right, and move through chronological 300-message pages. Message search covers the current page. Move the entire reading directory, including its JS/CSS, conversations, and assets. The embedded viewer needs Microsoft Edge WebView2 Runtime; a browser can open the same HTML offline.

Contact names prefer remarks, nicknames, aliases, then IDs when supported metadata is available. Recognized local images, legacy XOR image containers, and browser-supported local media can be shown. New encrypted images requiring a separate key, unsupported resource indexes, SILK voice, and unsupported contact tables remain visibly unavailable. Reading copies do not replace raw backups.

To upgrade an old reading export, reopen its verified raw backup sidecar and export into a fresh directory. The original account need not be copied again. Prefer 1 database worker on a hard drive; try 2 or 4 on an SSD. Export reports include phaseSeconds for full backup verification, database work, and reader indexing.

## Reading and appearance

Reading copies use conversation navigation, typed cards, missing-media placeholders and collapsed original content. Re-export a verified raw backup to apply the new parser to an old reading copy. See [message coverage](message-display.md).

Drag the log divider to resize. Manual scrolling or selection pauses following; the switch or Ctrl+Shift+L toggles it. Preferences offer monochrome Light, One Dark Pro, reduced motion and optional custom JSON colors. See [themes](themes.md).

## Rule-set library and compact controls

Preferences > Rule sets shows the name, source and catalog filename of each set. Import rule set accepts a version-1 rule catalog JSON. Imports stay in private data/rule-sets, separate from the installed catalogs. Check the sets you want, then Save settings. Removing an imported set removes it from the library on save; its stored catalog is retained. Cancel discards library changes. Packaged sets can be disabled. Duplicate rule IDs across selected sets are rejected; use local overrides when replacing an existing rule. Importing never scans or executes rules.

The sidebar menu icon switches between labels and icons; each icon has a tooltip. Drag its boundary to change width. Tables show a sort arrow in the active column. Click the header to reverse direction; size columns compare byte counts rather than formatted units.

While an operation runs, its initiating button becomes Cancel in the same location. The log can be collapsed with its chevron and resized using the thin divider. Log messages generated by Storloom use English regardless of the interface language; resource names, paths and external tool output retain their original text.

Find accounts searches common QQ/WeChat locations and registered WeChat/Weixin storage paths. A selected or known root containing a directory link is resolved to its physical local location; nested links remain excluded. If none are found, the page says so. Use Choose account folder for another location, selecting the complete account directory rather than an individual database or media subfolder.

Right-click a table header to fit that column, fit every resizable column, or restore the original widths. Hover a table cell to read its full value. Double-click a row in a checklist to toggle its checkbox; directory rows in storage analysis still open that directory. Icon actions have tooltips and no visible border.
