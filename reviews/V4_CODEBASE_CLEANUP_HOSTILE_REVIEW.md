# C4 codebase cleanup hostile review

Status: **zero unresolved C4 findings** on 2026-09-29. This signs off source, documentation, reproducibility, and prospective staging only. Phase 11's human visual and screen-reader gates and Phase 1's real-client PNG proof remain separate and open. No commit or Release build has been made.

## Findings resolved

1. The initial fresh checkout exposed CRLF-sensitive generated-contract snapshot tests. Both v3 and v4 snapshot comparisons now normalize line endings and retain exact content checks. The final staged checkout passed 183/183 tests.
2. The v4 record mutation service had compressed claim, note, lifecycle, and image-primary logic. It now has explicit validation, read, update, and primary-selection steps. Focused application, image, and protocol tests passed 16/16 after the refactor.
3. The v4 write-tool result and token helpers were too compressed to review safely. They now show success/error mapping, affected-reference construction, and deterministic token derivation explicitly. Focused protocol and backup tests passed 7/7 after this change.
4. Background diagnostics echoed startup exception messages, configured local paths, or arbitrary administrative command tokens. Those paths are now redacted at the server, viewer, and typed read-client boundaries. Three diagnostic tests pass.

## Current source and repository audit

- `README.md`, the design decisions, active v3/v4 contract guides, the migration ledger, startup instructions, generated snapshots, and the v4 progress plan consistently distinguish deployed v3 Release from Debug v4. The historical v3 checklist is archived. A scan found zero broken relative Markdown links and no active reference to a nonexistent Windows startup page.
- The web project references only the typed read client, not the Access adapter. The client requests `--read-only`; the v4 tool catalog omits mutations on that connection. A direct attempted mutation fails with `read_only`, and a storage read confirms it created no continuity. HTTP probes reject foreign Host/Origin and missing viewer headers; no mutation route exists.
- No active source contains a TODO, FIXME, HACK, or `NotImplementedException`. Large schema and Access modules remain cohesive by responsibility; the simple MCP adapter methods intentionally mirror stable named operations and are covered by generated contract snapshots. The timeline and record pages are separate browser modules.
- The final prospective index contains 203 entries. Its root and disposable indexes have identical path/mode/blob sets, `git diff --cached --check` is clean, and no staged path enters generated output, local configuration, database, downloaded tunnel client, browser profile, or user-private feedback. No file exceeds 1 MiB or contains a NUL byte. Path, key-shape, tunnel-identifier, and private-key-block scans found no hit. The audit was repeated after the documentation-only review edits.
- The exact staged clean checkout built with zero warnings/errors and passed **183/183 Debug tests, zero skipped**, in 10 minutes 9 seconds. Its binary exported surface `4.0` with 70 tools; all three generated JSON files matched the staged snapshots after line-ending normalization. Four disposable Chrome layout/keyboard probes passed. The current NuGet direct/transitive vulnerability query returned no advisory matches from the configured sources on 2026-09-29.

## Scope boundary

The Phase 11 consolidated visual and screen-reader review and the two real chat-client PNG ingress checks remain open in the main plan. The cold 50,000-event performance exception is documented and accepted for this Debug candidate; visual feedback can reopen it. Phase 11 and Phase 12 stay unchecked.
