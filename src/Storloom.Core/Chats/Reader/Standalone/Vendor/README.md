# Offline reader dependencies

SQLite3 Multiple Ciphers 2.7.0, SQLite 3.54.1, official non-SIMD WASM bundle:
https://github.com/utelle/SQLite3MultipleCiphers/releases/tag/v2.7.0
The downloaded ZIP was verified against the release SHA256SUMS.

Brotli-wasm 3.0.1, npm package from https://github.com/httptoolkit/brotli-wasm/tree/v3.0.1 . The web glue source retains upstream code and headers; line endings and trailing whitespace are normalized. Bundling removes ES export syntax, supplies embedded WASM bytes and wraps the API in a local closure. No network loader is used.

WASM files are base64 encoded byte-for-byte for embedding in a single local HTML document. Original JavaScript license headers are retained. Full bundled notices, including Emscripten and versioned Rust dependency licenses, are in NOTICE.txt and in generated reader HTML. Crate downloads were verified against the upstream Cargo.lock SHA-256 checksums.

SHA-256 of stored source files:

- `brotli.js`: `2819dd83aae895ce88f7bc5e353b814eead89294d2eed44f5a87525c8365fd03`
- `brotli.wasm.base64`: `aa9e3847b5b65a51b98e0246d9d1432c54e57cee33742e30e17665e16b382ce6`
- `NOTICE.txt`: `653f38139e704cd67dd4daa4f0e39a4c6038d521bd02d2e3b86ff6042eab7176`
- `sqlite3.js`: `cf2b4fa71a83dae18bed69174b0a77389d7cb607b78dae2533d707b72f9cca7d`
- `sqlite3.wasm.base64`: `6aea5b809b6b581f892bf33dd3bf1d6efbc9a5f84da3a0aad58edaf3a309bec5`
