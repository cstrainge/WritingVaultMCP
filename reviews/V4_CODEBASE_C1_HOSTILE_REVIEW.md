# C1 hostile review: active code ownership

Status: **zero open C1 findings** on 2026-09-29. This review covers Debug builds and disposable databases only. Release artifacts and production data were not changed.

## Findings and disposition

1. The root SDK project previously excluded 24 retired v1 prototype `.cs` files with individual compile rules. They are preserved as `.cs.retired` under ignored `artifacts/legacy-source-archive/`; the root project now excludes only the sibling projects, tests, tools, and artifacts that SDK globbing would otherwise compile. No active code refers to the archived prototypes.
2. The v4 read service had combined search, records, timeline, and history into one hard-to-navigate file. Its partial files now follow those responsibilities while retaining the same shared continuity-scope, revision, cursor, and semantic-reference policy. Contract generation and behavioral tests cover the resulting public surface.
3. The viewer controller had mixed record rendering, image loading, Markdown parsing, and timeline rendering with routing and live refresh. `record-pages.js`, `markdown.js`, and `timeline.js` now own those concerns; `app.js` remains the session, routing, search, and watcher controller. The Markdown module only constructs DOM nodes and permits HTTP(S) links. The record module obtains images and cached source text through the authenticated read-only API.
4. A path-identity test compared the first 16 bytes of two unrelated Access databases and falsely treated matching file headers as proof of aliasing. It now creates a disposable database and compares its drive-letter and volume-GUID paths. No usability or production database is read by the test.
5. Startup and bounded-log tests relied on source-string checks for behavior. The startup test now checks the generated task plan. A child-process probe checks actual credential redaction and log rotation; the existing crash probe checks kill-on-close containment. Static checks remain only for wiring and forbidden browser APIs where they are architectural constraints.

The remaining large files were reviewed for ownership before deciding whether to split them. `AccessSchemaDefinition` is one schema declaration; `V4ContractCatalog` is one generated-contract policy; `AccessBackupService` owns backup, verification, and retention; and the `AccessVaultService.*` partial files preserve the v3 compatibility surface by mutation/read concern. Further mechanical splitting would scatter transaction and compatibility invariants without a clearer owner. The cross-layer boundary remains: the web project references only the typed read-only client and has no Access writer dependency.

## Verification

- The solution builds in Debug with zero warnings and errors. The post-refactor full Debug suite passed 177/177. After the last boundary/log probe additions, the focused Debug gate passed 13/13.
- The generated v4 contract snapshot tests pass; both v3 snapshot files are byte-identical to the pre-refactor clean-checkout baseline. The three schema declaration/migrator files are also byte-identical to that baseline.
- Chrome opened the representative character page after the module split, rendered a Markdown emphasis and safe HTTPS link, loaded the image with alt text, and navigated to the focused timeline. No external script loaded. The dense-cluster browser probe retained keyboard focus after graph refresh.
- The `.editorconfig` and `.gitattributes` cover touched source and Windows launch scripts. No new dependency or migration was introduced by C1.

No C1 issue remains open. Phase 11 accessibility, clean-checkout, and repository-wide gates are separate.
