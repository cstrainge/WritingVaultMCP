# C2 hostile review: documentation and history

Status: **zero open C2 findings** on 2026-09-29. This review covers active Markdown and the prospective staged documents, not production deployment or the pending Phase 11 visual acceptance.

## Findings resolved

1. `DESIGN_DECISIONS.md` still described Phase 7, record pages, Markdown, galleries, and the timeline as future work. Their status now distinguishes Debug technical completion from pending human visual acceptance. The v3 Release verification is labelled as historical rather than v4 evidence.
2. `SCHEMA_MIGRATIONS.md` stopped its ledger before the Long Text and v4 migrations. It now names the complete ordered ledger and separates resumable additive upgrades from the exceptional empty-baseline rebuild.
3. Operations, runtime, and troubleshooting pages said every backup required a closed database. They now distinguish administrative CLI backups from the coordinated v4 MCP backup, which briefly quiesces writes and verifies companion assets.
4. The plan promised a separate Windows startup document that did not exist. Startup is already documented in `docs/WEB_VIEWER.md`; the deliverable now points to that single owner.
5. The installation guide had described ChatGPT desktop as tunnel-only. Current official ChatGPT desktop MCP guidance confirms direct local STDIO setup; the guide now separates desktop configuration from the web tunnel and keeps the deployed Release v3 instructions distinct from the future v4 cutover.

## Verification

- The documentation index identifies one authoritative guide for each topic; retired v2/v3 design and release material is in labelled archives. The original v3 feedback log is explicitly historical, while the image-ingress gate remains visibly open.
- A repository Markdown-link audit found 104 relative links and zero missing targets. Searches across active docs found no personal absolute paths, missing startup-document references, or stale phase status after the corrections.
- The prospective Git staging set has 197 files and no database, browser profile, downloaded tunnel binary, local configuration, log, or backup. Pattern scans found no personal absolute path, tunnel identifier, key, or private-key block. `temporal_aging.md` and test fixtures intentionally retain small Lostville acceptance examples; they are functional specifications, not a story manuscript.
- From a disposable staged checkout, the solution restored and built in Debug with zero warnings. The documented Debug CLI help command returned the current administrative commands. The full clean-checkout suite is part of C3 and is not claimed here.

No C2 issue remains open. C3 still requires a passing full clean-checkout test/launch and final staging audit.
