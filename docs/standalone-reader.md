# Independent HTML reading

Version 2.10 reads a v3 compact reading archive directly in a desktop browser, without Storloom, a server, network access or unpacking all media. The HTML is an entry point, not a self-contained copy of the chat content. Keep the entire archive directory when moving it.

Enable independent HTML reading under task details in the creation form, or select Independent HTML reading on an existing compact account. Open `阅读.html`, choose the directory containing archive.json and grant read-only access. Browser permission requires a user gesture. Compatible directory selection enumerates files first and can take longer on large directories. Cancelling selection is harmless.

Password envelopes and direct keys use the existing archive credentials. Password changes do not require rewriting content or regenerating HTML. Credentials and decrypted content are not persisted in a browser database. Appearance preferences use local storage when available. Lock / close terminates the Worker, clears the view and releases object URLs. JavaScript cannot guarantee physical erasure of every secret copy.

The existing reader provides conversations, paging, search, references, identity information and image previews. Single media files over 32 MiB are not buffered for inline playback. The save action streams chunks with the file-save API; compatible Blob downloads are bounded to small files. Save a large file and open it in an external player. Browser codec limitations still apply.

A read-only WASM VFS uses File.slice and FileReaderSync in a Worker against the existing query database. It does not load the full index into memory, copy it to OPFS or build duplicate search postings. Page and Blob caches have bounded budgets. Runtime components are embedded in one HTML. Small conversation summaries live in the existing index, and the protected snapshot manifest contains the reader metadata.

First preparation of an older archive aggregates conversation summaries from its index. Subsequent message commits maintain summaries incrementally. Enabling the feature during creation prepares it before publication. Native backups must first be parsed into reading archives. Archives without this optional feature do not pay its preparation cost.

Browser roots match a published HEAD and its archive-local query snapshot. Updates prepare a candidate before publishing HEAD; interruption preserves the existing reader. A detected snapshot change requires refresh. Compatible file selection captures File objects which may become stale after updates; select the directory again. Copying retains an available browser snapshot and index; reorganization and protection conversion prepare a corresponding entry for the destination. Missing old projections require explicit entry regeneration instead of a silent full history scan in the browser.

The CSP blocks network connections and remote media. Archive scripts are parsed as data, not executed. Local file tests on the installed independent Edge cover plain/password/direct protection, wrong credentials, paging, search, references, images, large-media bounds, locking and snapshot changes. No security switches or extra launch flags are required. Chrome uses the same APIs but was not installed for local verification; other browsers are not guaranteed. Directory authorization can be restricted by browser policy, in which case use compatible selection.

Common terms, late pages and slow disks can require more index work. Queries have a deadline and report failure. Small-fixture timings are not a promise for every operation on million-message archives.
