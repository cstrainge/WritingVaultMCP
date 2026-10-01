# Codebase cleanup and repository-readiness plan

Status: C0-C4 complete. C4's zero-finding source and repository review completed 2026-09-29; Phase 11's human review and Phase 12 cutover remain open.

This is the Phase 11 repository-readiness track in [V4_API_PLAN.md](../V4_API_PLAN.md). C0 establishes the baseline before Phase 9 code changes; C1 can then accompany Phases 9 and 10, and C2-C4 finish before a Phase 12 Release candidate. The existing Release build and production database remain untouched until cutover approval. A checked item requires evidence, and each cleanup phase needs a hostile review with zero unresolved findings before the next is checked.

## C0. Protect the baseline and classify the tree

- [x] Record a clean Debug baseline before moving source: server and web builds succeeded with zero warnings and errors; the full suite passed 158/158, including generated contract snapshots and disposable schema/integrity tests. The accepted Phase 8 disposable viewer preview supplies the visual smoke baseline.
- [x] Inventory active source, generated outputs, vendored tools, local configuration, databases, backups, browser profiles, logs, and user-authored story material by path and owner. The table below records disposition without private contents.
- [x] Prove the historical `testdata/WritingVault.Integration.Template.accdb` is unused: integration tests create fresh disposable databases with `AccessDatabaseFileInitializer`; only old Markdown refers to the template. Keep the file ignored and preserve it locally pending C2 archival/removal. No Access database is currently allowed into the prospective repository.
- [x] Establish initial ignore rules for `bin/`, `obj/`, `artifacts/`, `usability/`, `.tunnel-client/`, downloaded tunnel binaries, local configuration, lock files, logs, backups, and all databases. The rules were exercised in a disposable Git repository; the real staging audit remains in C3.
- [x] Identify documents and scripts referring to retired paths or the production-specific user/workspace path by a repository-wide Markdown/PowerShell/batch/JSON scan. The affected active scripts are `Configure-WritingVaultStartup.ps1`, `Run-WritingVaultTunnel.ps1`, and `Run-WritingVaultWeb.ps1`; the document candidates are listed in C2 and the C0 inventory below. Preserve local operational settings outside the future repository.

| Path group | Disposition |
| --- | --- |
| `Application/`, `Domain/`, `Infrastructure/`, `Mcp/`, root server project | Active runtime plus explicitly excluded legacy source; C1 identifies and removes only proven dead files. |
| `WritingVault.Client/`, `WritingVault.Web/`, `tests/`, root launchers, `tools/` | Active client, viewer, tests, and operations; keep and refactor/document as needed. |
| `contracts/` | Generated contract evidence; keep only generator-matching snapshots. |
| `bin/`, `obj/`, `artifacts/`, `usability/` | Local build output, reports, browser profiles, and disposable databases; ignore. Preserve any needed evidence through reviewed prose, not bulk profile/database copies. |
| `.tunnel-client/`, `mcp-tunnel/`, `appsettings.json` | Local tunnel profile, downloaded binaries, and machine configuration; ignore. Commit only sanitized instructions and the example configuration. |
| `testdata/` | Unused historical Access template and stale readme; ignore the database, review both in C2, and generate all integration databases from code. |
| `feedback.md`, `timeline.md`, `PHASE_0_BASELINE.md` | User-authored or private historical material; ignored until a deliberate private/archive or sanitized-public decision. |
| `temporal_aging.md`, active design/feedback docs, `reviews/` | Needed specifications and evidence that include fictional examples or local history; keep available for development and review their staged contents in C3 before any commit. |

**Exit gate:** The baseline is repeatable, every potentially private or generated path has an explicit disposition, and no code or database behavior has changed. Hostile review: zero unresolved findings.

Evidence: [C0 hostile review](../reviews/V4_CODEBASE_C0_HOSTILE_REVIEW.md). The 158-test Debug baseline and both builds passed; the disposable Git ignore probe passed after the unused Access template exception and owner-lock omission were corrected.

## C1. Refactor active code without changing the public contract

- [x] Audit the files named in `WritingVaultMcp.csproj` `Compile Remove` entries. The 24 retired v1 prototype files are preserved as `.cs.retired` under ignored `artifacts/legacy-source-archive/`; only sibling projects, tests, tools, and artifacts remain excluded from root SDK globbing.
- [x] Split `AccessV4ReadService.cs` by coherent read responsibility: search, overview/relations, timeline, and history/change feed. The shared semantic-reference, continuity-scope, revision, and cursor policy remains in the core partial type.
- [x] Review the large `AccessVaultService.*`, schema, backup, MCP mapping, and contract-catalog files for duplicate validation, repeated SQL mapping, dead compatibility paths, and hidden cross-layer dependencies. The remaining files have cohesive owners; the review explains why further mechanical splitting would scatter transaction or compatibility rules.
- [x] Separate routing/session and live-refresh logic from record pages, safe Markdown, image loading, and timeline rendering. The viewer now has `app.js`, `record-pages.js`, `markdown.js`, and `timeline.js`; Chrome and read-only boundary probes passed.
- [x] Add a small `.editorconfig` and format touched code consistently. Document non-obvious invariants, transaction boundaries, date semantics, cursor rules, and why a read is scoped; avoid comments that merely restate code.
- [x] Replace source-string-only tests with behavior or architecture checks where the distinction matters. The path alias, task-plan, log rotation/redaction, browser, and process-containment probes exercise behavior; static checks remain for architectural boundaries.
- [x] Run focused tests after each extraction, then the full Debug suite; compare v3/v4 generated declarations and verify no database migration or public shape changed unintentionally. The full suite passed 177/177 after the refactor, focused tests passed 13/13 after final test additions, v3 snapshots and schema declarations are byte-identical to the earlier clean-checkout baseline, and v4 snapshots are generator-verified for the separately reviewed Phase 10 API additions.

**Exit gate:** Active modules have clear owners, retired source is absent from normal builds and navigation, and the public contracts and supported behavior are unchanged except for separately reviewed v4 feature work. Hostile review: zero unresolved findings.

Evidence: [C1 hostile review](../reviews/V4_CODEBASE_C1_HOSTILE_REVIEW.md).

## C2. Consolidate documentation and preserve history

| Current material | Intended disposition |
| --- | --- |
| `README.md`, `V4_API_PLAN.md`, `DESIGN_DECISIONS.md` | Keep as the entry point, progress ledger, and accepted decisions. Remove stale release counts, stale phase language, and machine-specific example paths. |
| `docs/V4_TOOLS.md`, `docs/V4_CONTRACT.md`, `docs/WEB_VIEWER.md`, `docs/OPERATIONS.md`, `docs/TIME_AND_DATES.md`, `docs/MCP_CLIENT_INSTALLATION.md`, `docs/TROUBLESHOOTING.md`, `docs/ACCESS_RUNTIME.md`, `docs/INCIDENT_RECOVERY.md`, `docs/SCHEMA_MIGRATIONS.md` | Keep as the current task-oriented and operational reference; reconcile each with the generated v4 contract and tested commands. |
| `docs/V3_TOOLS.md` | Keep as the active, explicitly labelled v3 compatibility reference for the deployed Release client. |
| `docs/V2_DATA_MODEL.md`, `DESIGN.md`, `IMPLEMENTATION_PLAN.md`, `docs/RELEASE_CHECKLIST.md`, older `reviews/phase*.md` | Moved to `docs/archive/` and `reviews/archive/` with historical indexes. The all-checked v3 release checklist does not certify v4. |
| `PHASE_0_BASELINE.md` | Preserve as ignored local historical and machine-specific evidence; do not stage it without a privacy review. |
| `feedback.md`, `timeline.md`, `temporal_aging.md` | Preserve user-authored requirements and story material. Keep the temporal specification linked; keep feedback and fiction ignored until their repository/private-archive destination is decided. Never discard them as duplicate documentation. |
| `docs/API_FEEDBACK_LOG.md`, `docs/V4_TEST_FIXTURES.md`, `docs/V4_IMAGE_INGRESS_PROBE.md`, `docs/V4_TOOL_DISPOSITION.md`, `docs/CODEBASE_CLEANUP_PLAN.md`, `reviews/V4_*.md` | Keep active requirements, unfulfilled probes, fixture specifications, tool disposition, cleanup ledger, and review evidence, but label proposal versus verified implementation precisely. |
| `contracts/v3/`, `contracts/v4/` | Keep generated, test-verified snapshots and their generation instructions; never hand-edit generated JSON. |
| `testdata/README.md` and historical Access template | Stale instructions were moved to `docs/archive/ACCESS_TEMPLATE_README.md`. The unused Access template remains ignored and local pending a safe disposition; never stage the database. |

- [x] Establish a short `docs/README.md` index naming one authoritative page for each topic and an archive index for historical evidence. Added `docs/archive/README.md` and `reviews/archive/README.md`; the link audit is still a separate gate below.
- [x] Correct contradictory statements: the README's old 84-test Release gate; future-tense Phase 7 text in `docs/V4_CONTRACT.md`; the still-open real-client image-ingress proof after Phase 6; and old v3 release checkboxes. State current v3 production versus Debug v4 accurately. The preserved v3 feedback log now explicitly labels its original watchlist as historical.
- [x] Reconcile overlapping tool, design, operation, and installation pages; merge truly duplicate prose, archive superseded versions, and keep a single link to each generated contract. The documentation index assigns a distinct owner to the contract, workflow, web, setup, time, and administrative operations guides; historical pages are labelled and indexed separately.
- [x] Document Windows startup in `docs/WEB_VIEWER.md` using the existing `Configure-WritingVault-Startup.bat` commands and the tested fixed-port, independent-task behavior. Production installation remains a Phase 12 operation.
- [x] Audit every Markdown link and local path after moves. Check that examples build from a clean checkout and do not expose personal paths, credentials, or story content unintentionally. All 104 relative Markdown links resolved, the clean-checkout Debug restore/build succeeded, and the documented `dotnet run --no-build --configuration Debug -- --help` command returned the current CLI. The prospective staged set has no personal absolute paths, credentials, or Access files; the retained Lostville acceptance examples are intentional fixture/specification content, not a manuscript.
- [x] Review the remaining documentation as a new contributor: purpose, supported platform, architecture, setup, disposable testing, v3 compatibility, Debug-only development, and release process must be discoverable from the README. The README and `docs/README.md` provide that route; active references distinguish the deployed v3 Release artifact from Debug v4, and the archived v3 checklist cannot be mistaken for v4 acceptance.

**Exit gate:** Every document is current, explicitly historical, or intentionally private; no misleading active checklist or broken link remains. Hostile review: zero unresolved findings.

Evidence: [C2 hostile review](../reviews/V4_CODEBASE_C2_HOSTILE_REVIEW.md).

## C3. Make a clean checkout buildable and safe to commit

- [x] Add a sanitized `appsettings.example.json` and instructions for creating the ignored local `appsettings.json`; keep production database and backup paths out of committed defaults. The example parses as JSON; staging behavior is checked later when a Git repository exists.
- [x] Add `.gitattributes` for text line endings and binary file handling. Tests generate Access databases through ADOX; `.gitignore` excludes all `.accdb` files from the prospective repository.
- [x] Define one solution/workspace entry point for the server, typed client, web viewer, and tests; ensure root build/test commands select the intended projects and use Debug during development. `WritingVault.slnx` includes all four projects, and the staged checkout restored, built with zero warnings, and passed 179/179 tests in Debug.
- [x] Document how to obtain external tunnel clients rather than committing downloaded executables, browser profiles, DPAPI material, or local tunnel configuration. Preserve required third-party license notices with the installation instructions. The guide links the official download, names the local license/notice inventory, and the staged tree includes none of the downloaded distribution or credential/profile files.
- [x] Run a clean-checkout rehearsal in a separate disposable directory: restore, Debug build, contract generation check, tests, and viewer launch against a disposable fixture. The generated v4 JSON matched all three checked-in snapshots; the viewer returned HTTP 200 and ready health against its own disposable Lostville fixture. The production database and Release output were untouched.
- [x] Perform a prospective staging audit in a disposable Git repository: inspect every file that would be committed, scan for keys/tokens/private paths and binary databases, and verify that no ignored local artifact is staged. The root repository was then staged without a commit, and its index entries matched the disposable index exactly. No forbidden file, binary content, personal absolute path, tunnel identifier, API key, or private-key block was found. Phase 12 still requires a verified immutable release commit.
- [x] Prepare `docs/RELEASE_MANIFEST_TEMPLATE.json` tying server, typed client, viewer, contract snapshot, schema migration, source commit, and verification results to release artifacts. It is deliberately marked `template-only` with null hashes; populate and verify the actual manifest during Phase 12 after an approved Release build.

**Exit gate:** A new contributor can build and test from the prospective repository contents alone, and the disposable staging list contains no secrets or personal data. Hostile review: zero unresolved findings. This gate means *ready to commit*; the root Git repository has been initialized but has no commit. Creating and verifying the immutable release commit remains a Phase 12 prerequisite and does not itself authorize production cutover.

Evidence: [C3 hostile review](../reviews/V4_CODEBASE_C3_HOSTILE_REVIEW.md).

## C4. Final cross-cutting review

- [x] Compare actual code and docs to the authority map, generated tool catalog, schema migrations, launchers, and tests; remove remaining dead branches and stale references found during the audit. The earlier C4 check found no broken relative link or retired active checklist; Phase 11's expanded 76-tool Debug catalog requires the final staging/link audit again.
- [x] Review source boundaries, error handling, cancellation, resource disposal, logging, cursor stability, privacy, and documentation from a fresh-maintainer perspective. The read-only boundary, diagnostics redaction, revision-bound cursors, and disposable storage behavior have focused tests and hostile probes.
- [x] Run the full Phase 11 Debug technical quality gate and record the exact results and any measured exceptions. The final staged checkout built without warnings/errors and passed 183/183 tests; four browser geometry/keyboard probes and loaded-page accessibility scans passed. The cold 50,000-event graph measured 987 ms versus the provisional 750 ms target; its Phase 11 disposition remains open.
- [x] Write `reviews/V4_CODEBASE_CLEANUP_HOSTILE_REVIEW.md` with findings, fixes, verification, and zero unresolved C4 source/documentation/staging findings.

**Exit gate:** The project is understandable, documented, reproducible, and safe to stage. Phase 11 and Phase 12 remain unchecked until their own gates are met.
