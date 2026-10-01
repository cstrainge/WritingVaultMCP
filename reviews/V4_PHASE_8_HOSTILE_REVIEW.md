# v4 Phase 8 hostile review

Date: 2026-09-29  
Result: PASS - zero unresolved technical Phase 8 findings  
Phase status: complete; user visual acceptance recorded on 2026-09-29

## Boundary attacked

- Tried to reach database and mutation services from the web projects, request a mutation over a read-only v4 connection, bypass the fixed loopback host, forge Origin and Host headers, omit the browser-session marker, submit unknown API routes, and exceed the request-body limit.
- Exercised independent tabs, duplicated-tab session tokens, continuity switching, per-tab story time, nonexistent and repeated daylight-saving times, route restoration, search refresh, stale cursors, aborted watches, backend replacement, and external writes from another MCP connection.
- Inspected the browser bundle for dynamic HTML injection, script evaluation, remote assets, external image fetches, unsafe content-security policy allowances, inaccessible focus behavior, motion without a reduced-motion path, and narrow-screen layout failure.
- Attacked fixed-port startup, launcher mutexes, bounded logging, credential redaction, child-process orphaning, startup validation, backend drain, tunnel-profile duplication, and rollback behavior.
- Registered two uniquely named disposable current-user Scheduled Tasks, forced each action to fail independently, verified the companion heartbeat continued, verified each failed task restarted exactly once, and removed both tasks and their temporary files.

## Findings resolved during review

1. The PowerShell 5 process API lacked `ProcessStartInfo.ArgumentList`. The bounded-process wrapper now detects support and uses quoted `Arguments` safely on Windows PowerShell 5.
2. Killing a background supervisor could orphan its child. Each supervisor now assigns its child to a kill-on-close Windows Job Object; the hostile process test proves the child dies with the supervisor.
3. Continuity bootstrap incorrectly required a selected continuity. Vault-scoped continuity reads now work before session selection.
4. Page revisions were encoded for a different cursor scope than `changes_since`. Visible pages now return a change cursor, with a regression test using the page revision directly in the watcher call.
5. Hidden HTML sections remained visible because layout rules overrode the browser's default hidden style. The visual system now enforces `[hidden]` consistently.
6. Duplicating a browser tab copied its session token. A same-origin `BroadcastChannel` collision handshake assigns the duplicate an independent token before bootstrap.
7. Search refresh could lose the active type and heading, and an older in-flight watcher response could overwrite a newer page cursor. Search state is retained and stale watch responses are discarded.
8. The browser had a per-session time API but no usable control. The shell now provides a per-tab local story-time dialog with timezone conversion, repeated-time choice, gap rejection, and a continuity-clock reset.
9. A bad story-time entry initially stopped live refresh, and validation initially invalidated a healthy backend session. Validation now happens before session acquisition and the watcher resumes with the existing state after a rejected entry.
10. Port collision logging occurred before confirmed host startup and cancellation could surface as noisy shutdown errors. Startup readiness, reaper cancellation, and fixed-port failure handling are deterministic.
11. ASP.NET request diagnostics produced several log lines per twenty-second watch heartbeat. Framework request logs are now filtered below warning while bounded operational logs remain available.
12. The first literal scheduler probe proved that `RestartOnFailure` does not reliably restart a manually started long-running task on this Windows installation. Each production task now also has an independent one-minute repeating watchdog trigger with `MultipleInstances IgnoreNew`. The corrected two-task probe passed in both failure directions.

## Verification evidence

- Final Debug build: 0 warnings, 0 errors.
- Focused Phase 8 Debug tests: 10 passed, 0 failed, 0 skipped.
- Full Debug suite: 158 passed, 0 failed, 0 skipped.
- Live HTTP probes: ready bootstrap; continuity selection; per-tab time set; daylight-saving gap rejected with 400 while the session survived; unknown API 404; foreign Origin 403; wrong Host 400; unchanged watch heartbeat returned without cursor expiry.
- Fixed-port probe: the second host exited 20 with `Writing Vault viewer could not start because http://127.0.0.1:5284 is already in use.` The first host remained available.
- Scheduler isolation probe: `webRestartCount: 2`, `tunnelRestartCount: 2`, and both companion-availability assertions true.
- Visual captures: desktop overview, narrow mobile overview, restored Characters route, and per-tab story-time dialog were inspected from the disposable Phase 8 database.
- Release binaries and the production database were not rebuilt, replaced, opened for writes, or used by the preview.

## Visual acceptance

There are no unresolved technical findings. The user reviewed the running shell and accepted its visual design on 2026-09-29, closing Phase 8.
