# Chat archive workflow

Implemented in 2.4. Global tabs contain Account archives, Database key library, and Accounts and clients. Adding or connecting an account is an action inside the account page, not a task wizard.

Account details expose reading creation/open, update, protection/encryption/decryption, raw backup, verification, restore, current task and history. Tasks go directly to their settings. Continuation is explicit and blocks incompatible new tasks without changing another button's meaning. Back and completion return to the same selected account. Narrow layouts replace the account list with a compact selector.

Database keys are password-protected files with independent passwords and explicit custody. The library page acquires/imports keys for a chosen account; setup retains a scoped chooser. Keys must authenticate the selected source database, and secrets are never automatically saved. Client discovery settings use a shared editor in their own tab and preferences dialog.

Reading updates and raw backups remain independent outputs. Direct reading holds source databases read-only and checkpoints their fingerprints. Managed protection and decryption publish new generations and retain historical readers. Raw backup remains suitable for client-data restoration; reading copies do not restore native client databases.

Packed stores still import completed readers. Advanced format operations remain collapsed; routine reading, protection, backup and key actions are visible. Connected encrypted readers decrypt to a separate plain copy. Previous encrypted copies retain their original protection.
