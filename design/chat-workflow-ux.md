# Chat archive workflow

Status: 2.2 implements the account center, independent task pages, separate raw and reading output locations, and explicit account size measurement.

## Organize by account, act by task

Use one account list as the home page. Adding an account and connecting existing material are entry actions. Storage format, import routes and first-time setup should not compete as navigation peers. Keep settings and the key library in a consistent tools entry. An account can own a live source, multiple raw backup generations and multiple reading versions.

The account detail shows source availability, last raw backup, last reader update, protection and unfinished tasks. Open reading is primary when a reader exists; otherwise create reading is primary. Updating a reading copy and backing up original files are independent tasks. Restore belongs to the selected raw backup generation.

| Task | Source | Output | Steps |
| --- | --- | --- | --- |
| Create or update reading copy | Local account or explicit raw generation | Readable messages and resources | Choose source, unlock, capture consistent snapshot, merge changes, apply protection, publish |
| Back up originals | Local account | Restorable raw generation | Detect changes, copy changed files, verify copied data, commit generation |
| Restore originals | Completed raw backup | New self-contained account directory | Check dependencies, choose new destination, copy and verify, adopt in client |
| Adjust protection | Existing reading copy | Version with explicit protection scope | Unlock, choose scope, transform, verify and publish |

Archive-library format is a storage choice, not a prerequisite concept for ordinary exports. Expose it in advanced creation or migration options, remember it for the account, and explain compatibility boundaries.

## Give each task its own settings

Replace the fixed account-backup-export wizard with task-specific pages. Reader settings contain source, destination, database access, update strategy and protection. Raw backup settings contain destination, generation and retention. Reuse known account material without repeatedly asking for its location.

Source selection is explicit: use a local source when available, or choose a dated backup generation. Never silently substitute an old backup for current source data. Prefer registered protected key files; unlock and authenticate them before reuse in task memory. Show acquisition and manual entry when no matching key is available. If acquisition needs a running client, do that first, then close the client for a consistent snapshot.

Direct export still needs consistency, but should not force a durable full raw backup. Merge changed messages and resources without deleting archived history. Distinguish first-time conversion, index rebuilding and actual incremental updates.

## Capacity and task state

Keep discovery fast and measurement explicit. Measure all or selected accounts from metadata, show logical size, file count, measurement time and completeness, sort without changing selection, and retain lower bounds after interruption. Unmeasured is not zero.

Account details expose task state and resume actions; the log is diagnostic rather than the only status surface. Report meaningful phase work, bytes and throughput. Show inventory as indeterminate and estimate remaining time only when totals support it.

Pause keeps an in-process task. Cancellation retains committed checkpoints; document restart-resume support per task. Resume binds to the captured source snapshot rather than silently collecting later changes.

## Existing material and compatibility

Existing registrations, original backups and reader versions remain in place. Raw and reading output locations are independent. Existing tasks retain their captured backup and workspace rather than changing source during the interface migration.

Independent reading tasks hold read locks on the closed client databases and transaction files, retain content fingerprints and do not create a full raw backup. Resuming compares fingerprints; changed databases or missing fingerprints stop the task while preserving prior readers and workspace. Once reading generation is complete, protection and publication no longer require the local source online.

Packed storage still imports or synchronizes from a completed reader; native client parsing is not connected directly to packed import. Associated packed material appears under the same account. If its source reader has advanced, the row explicitly reports pending synchronization and opens the newest reader rather than presenting stale packed data as current. Format actions provide an explicit synchronization route.

Forms use compact grouping and fixed action footers. Preferences separate general, tasks/logs and rules; account location settings separate search and client programs. Main task forms fit regular and narrow windows without whole-form scrolling. Long lists and exceptionally small windows retain scrolling with a dedicated gutter, never covering controls.
