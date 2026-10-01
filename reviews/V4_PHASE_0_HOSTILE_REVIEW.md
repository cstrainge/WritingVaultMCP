# v4 Phase 0 hostile design review

Review date: 2026-09-28  
Scope: v4 MCP contract direction, storage additions, read-only web companion, timeline, image flow, migration, compatibility, and Phase 0 deliverables  
Verdict: **Pass - zero unresolved findings**

This is a design review, not implementation evidence. Each resolution below is now an accepted constraint in `DESIGN_DECISIONS.md` and an implementation or verification item in `V4_API_PLAN.md`. A later phase must reopen the finding if implementation evidence contradicts the design assumption.

## Findings and resolutions

| Finding | Risk | Resolution | State |
| --- | --- | --- | --- |
| The viewer could become a second Access owner. | Lock conflicts, inconsistent reads, bypassed coordination. | The viewer uses a typed read-only MCP connection to the elected backend and has no OleDb dependency. | Resolved |
| Omitting write tools alone is a brittle read-only boundary. | A fabricated request or accidental service reference could mutate data. | Read-only is checked at tool registration, connection capability, application dependency, and negative database-change tests. | Resolved |
| A local HTTP listener can still be targeted cross-origin or through DNS rebinding. | Story data could be read by hostile browser content. | Bind only to `127.0.0.1`, validate Host and Origin, disable CORS, use same-origin state and strict CSP, and expose no arbitrary proxy. | Resolved |
| Dynamic or ephemeral ports make copied deep links unreliable. | Side-by-side chat links stop identifying the page after restart. | Use fixed default port 5284 with explicit override and fail clearly on collision. | Resolved |
| Browser tabs could share mutable MCP session state. | Continuity or artificial time leaks between views. | Each browser circuit has its own backend connection; route continuity re-establishes state after reconnect. | Resolved |
| Old and new clients could see a mixed contract. | Cached schemas call incompatible tools or shapes. | Negotiate exactly one requested surface in the pipe handshake; reject unsupported versions. | Resolved |
| “v3 fallback” could imply an old binary may open the v4 schema. | Exact-schema failure or unsafe downgrade. | Only the v4 binary supplies the compatibility surface after migration. Old binaries are not schema-compatible. | Resolved |
| A blanket ban on fields ending in `Id` also rejects legitimate domain values. | Useful timezone/calendar semantics become awkward or misleading. | Ban internal storage identity, not the suffix. Allow documented domain identifiers such as `referenceTimeZoneId` and `calendarId`; contract tests use a semantic allowlist. | Resolved |
| Natural-name convenience could silently choose the wrong record. | Mutations affect unintended canon. | Resolve only exact unique normalized matches in context; ambiguity returns labelled candidates and performs no write. | Resolved |
| Only world events could be linked to projects. | A project page would omit character, location, organization, or object events that happen during that story, while forcing users to duplicate them as world events. | Permit optional many-to-many project associations for both event kinds, keep unassigned events valid, enforce same-continuity links, and expose one `event_project_apply` abstraction over `ProjectEntities` and the new `EntityEventProjects` table. | Resolved |
| A generic write API could bypass temporal and relationship invariants. | Overlaps, one-way relationships, or broken ownership histories. | Keep specialized domain commands; use common operations only for genuinely uniform actions. | Resolved |
| Reducing mutation-token friction could destroy uncertain-retry safety. | Duplicate writes after reconnect or timeout. | Keep explicit common `mutationToken` until host-provided identity is proven stable across supported clients. | Resolved |
| Timeline rendering could require an artificial clock. | An unset Lostville clock would make historical data unusable. | Historical and fuzzy chronology always renders. Only the current-time line, current-state projections, and age calculations report unset time. | Resolved |
| Timeline graphics could invent precision for fuzzy dates. | The UI communicates false canon. | Use distinct points, bars, precision bands, open edges, uncertainty styling, narrative-only mode, and an undated collection. | Resolved |
| Story time and operational timestamps could be mixed. | Audit activity is mistaken for fictional chronology. | Keep story timeline and real-UTC history in separate contracts and views. | Resolved |
| Image storage design could discard the uploaded original. | Irrecoverable quality or metadata loss after conversion. | Preserve immutable, content-addressed masters outside Access; store only bounded viewing renditions in Access. | Resolved |
| External file and Access commits cannot be one atomic transaction. | Orphans or broken references after failure. | Hash and atomically place immutable content first, then commit its DB reference. Orphans are harmless and collectible; missing referenced hashes are integrity failures. | Resolved |
| Access image growth could reach the 2 GB file limit. | Writes fail or the database becomes fragile. | Enforce rendition/input ceilings, health capacity bands, warning/hard-stop limits, and capacity fixtures. | Resolved |
| Chat attachments may not arrive as local paths or even use the same shape in Claude and ChatGPT. | Phase 6 could build an unusable ingestion API. | Phase 1 requires a real non-persisting PNG ingress probe through both clients and adapter normalization before image storage work. | Resolved |
| Markdown and URLs can carry executable or tracking content. | XSS, data exfiltration, or automatic remote requests. | Disable raw HTML and remote image loading, sanitize output, reject unsafe schemes, and apply CSP. | Resolved |
| Selected-continuity change polling could omit vault-global sources and tags. | A visible entity page remains stale after a linked source/tag edit. | Return selected-continuity changes plus all changed vault-global source/tag references and invalidate matching visible sections. | Resolved |
| A stale or backend-specific change cursor could break refresh after restart. | Viewer silently stops updating. | Cursor expiry/restart yields an explicit full-refresh response and a new opaque cursor. | Resolved |
| A write could commit between the initial page read and establishment of its watcher. | The page remains stale even though the live feed appears healthy. | Every page returns its observed revision and watching begins from that revision, making an intervening commit immediately discoverable. | Resolved |
| A dead watcher could leave an old snapshot looking current. | The user unknowingly reasons from stale canon. | Live is conditional on watcher health and applied revision; failure produces a persistent stale warning and catch-up is required before Live returns. | Resolved |
| Requerying several sections could render a mixed revision. | One page temporarily presents internally inconsistent canon. | Mark affected sections updating and advance the applied revision only after replacement data is rendered; unknown impact forces a full visible-page refresh. | Resolved |
| Server push or long polling may behave differently across MCP implementations. | Refresh reliability depends on unverified transport behavior. | Use ordinary bounded polling: two seconds while visible, backoff while hidden, immediate refresh on focus. | Resolved |
| A page link alone may not tell a chat assistant which record the user means. | “Update this” remains ambiguous. | Copy-reference includes label, kind, continuity, and semantic reference; it contains no secret or database identity. | Resolved |
| One graph truncation flag hides which collection is incomplete. | Pages silently omit relationships or notes. | `get` returns independently paged sections and `list_related` continues one section. | Resolved |
| Cross-type search can blur vault-global and continuity-scoped results. | Sources appear to belong to a continuity falsely. | Results label kind and scope; canon is selected-continuity only while sources/tags remain explicitly vault-global. | Resolved |
| A unified notes table migration could break existing note/claim foreign keys. | Reference loss or complex populated-table rebuild. | Add physically separate continuity notes and provenance tables behind one application note model. | Resolved |
| Large timelines could overload Access, MCP payloads, or the browser. | Slow or unusable global view. | Query by viewport, cap pages, aggregate wide views, cancel stale zoom requests, and test 50,000 projected items. | Resolved |
| Automated screenshots cannot determine whether the UI feels coherent. | A technically correct viewer ships with poor navigation or visual hierarchy. | The user must visually accept concrete running work at the end of Phases 8, 9, and 10 before the next layer closes. | Resolved |
| v4 scope could expand into accounts, public hosting, or a full editor. | Delivery stalls and threat model changes midstream. | Deferred work is explicit; the v4 web application remains a local read-only companion. | Resolved |
| Migration could strand production on an unreadable schema. | Data loss or prolonged outage. | Explicit administrative migration, verified backup plus asset manifest, representative populated/interrupted fixtures, exact verification, and rollback drill remain release gates. | Resolved |
| Running background processes as a machine service would cross the current-user pipe and DPAPI boundary. | Tunnel credentials fail or become accessible in the wrong security context. | Use hidden current-user Scheduled Tasks triggered at logon; pre-login service hosting is deferred. | Resolved |
| A startup task could expose the tunnel API key in arguments or task XML. | Local credential disclosure. | Require the existing current-user DPAPI file and inject plaintext only into the child process environment; inspect task definitions in tests. | Resolved |
| Re-running the tunnel task after failure could create duplicate profile instances. | Remote calls alternate between independent session states. | Keep the profile mutex/process guard authoritative and test concurrent scheduled retries. | Resolved |
| Silent port fallback would make the viewer bookmark unstable. | The saved URL stops working after a collision or restart. | Reserve fixed default `127.0.0.1:5284`; fail clearly on collision and change it only through explicit configuration. | Resolved |
| Coupled supervision could bounce a healthy viewer when the tunnel crashes, or vice versa. | One fault unnecessarily removes both access paths and their sessions. | Use two independent tasks and restart policies. Only explicit maintenance commands coordinate their lifecycle. | Resolved |
| Automatic restarts could fight closed-database migration or restore work. | Background adapters reopen Access during administration. | The startup manager coordinates stop/restart, suppresses retries while stopped, and waits for backend drain. Startup never migrates automatically. | Resolved |
| Partial task installation could leave one stale background component. | Tunnel or viewer behavior differs from reported configuration. | Validate all prerequisites before mutation and roll back tasks created during a failed installation attempt. | Resolved |

## Phase 0 evidence

- Accepted v4 decisions are recorded in `DESIGN_DECISIONS.md` sections 15–22.
- All 68 v3 public tools have a disposition in `docs/V4_TOOL_DISPOSITION.md`.
- Representative functional, scale, threat, migration, image-ingress, and visual-review fixtures are defined in `docs/V4_TEST_FIXTURES.md`.
- Deferred scope and human review gates are explicit in both the decision record and implementation plan.
- The implementation plan includes the updated identifier rule, two-client image ingress gate, unset-clock timeline behavior, global-record refresh behavior, and visual reviews for Phases 8–10.

No Phase 0 design issue remains open. Phase 1 may begin, but no later phase inherits a pass automatically; each retains its own hostile-review exit gate.
