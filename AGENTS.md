# Storloom

[简体中文审阅版](AGENTS.zh-CN.md)

Storloom is a Windows storage manager written in C#. GUI and CLI share the core engine.

- Read-only analysis may inspect any mounted local directory. Mutations require reviewed plans and explicit confirmation.
- Rules are JSON data, never executable scripts. Preserve identity, age, process, link, boundary, and expiration checks.
- Transfers must verify the complete destination before any source removal. Do not merge existing destinations or silently skip backup files.
- Chat export never deletes its source. Preserve unsupported payloads and report incomplete results. Never log or persist captured keys.
- Keep client-specific native integration isolated. Retain upstream copyright, license, and provenance notices.
- Do not add developer paths, account identifiers, chat data, secrets, personal Git identity, scan results, or machine configuration to source or packages.
- Runtime state belongs in ignored `data/`, `.local/`, or `artifacts/` directories. Build packages from an explicit allowlist.
- Tests create isolated fixtures beneath their output directory. Never use real user caches, accounts, or backup folders as test fixtures.
- Maintain English and Simplified Chinese resources together. Keep schema, rule guide, and behavior consistent.
- Dialog and utility-window footer actions belong at the bottom right, with aligned baselines, consistent heights, and even spacing; the final action must align with the content's right edge. Use a shared layout helper rather than independently positioning buttons.
- Search/form labels and their controls must be vertically centered, separated by visible spacing, and aligned to the same content margins. Avoid laying out a text label and an input in an unaligned wrap panel. Check both languages, light/dark themes, and DPI scaling using the rendered UI before delivery.
- Validate with `dotnet build Storloom.sln -c Release` and `dotnet test Storloom.sln -c Release` on Windows. See README for packaging.
