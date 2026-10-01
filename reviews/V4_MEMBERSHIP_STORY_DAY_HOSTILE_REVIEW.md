# Debug v4 membership transitions and story-day hostile review

Status: zero open findings in this scoped review, 2026-09-30. This is not Phase 12 Release approval.

Scope: additive schema 008; relationship and organization join/leave descriptions; organization transition reads and writes; date-only viewer session, age bounds, and timeline marker. Release binaries and production data were not changed.

| Probe | Adversarial question | Result |
| --- | --- | --- |
| Migration | Does upgrading a populated 007 database alter old rows or invent exact join/leave dates from fuzzy periods? | Fixed 007 checksum; 008 adds tables and backfills only exact old periods. The populated migration and exact-versus-fuzzy tests pass. |
| Membership boundary | Can an exact exit moment overlap a replacement or disagree with its stored period? | A test found the envelope included the exit instant while the closed period excluded it. The envelope now excludes exact exit instants; the transition and integrity checks pass. |
| Partial legacy transition | Does adding only an exit erase an older uncertain period from the timeline? | Fixed: the period remains visible until both named transitions are known. |
| Descriptions | Can one transition description overwrite the other or survive an explicit clear? | Focused relationship and organization tests cover independent text, generic fallbacks, clearing, and atomic transition updates. |
| Date precision | Does a date-only selection silently become midnight, including on a daylight-saving day? | The v4 session reports `DateOnly` with no instant. Character age spans the full local story day; the Vancouver spring-forward test checks the calendar-day bounds. Story-date aging remains measured on timezone-free local clock values, so its displayed duration is 24 story hours on that date. Exact current state is withheld with an explanation. |
| Viewer | Can the clock time be entered by keyboard without the native time scroller, and can same-day generic transitions be grouped without swallowing custom prose? | Separate date and 24-hour text inputs replace `datetime-local`; browser grouping, date label, and script-parse probes pass. |
| Contract | Are the new operation and fields actually advertised? | Debug v4 export reports 80 tools and the contract snapshot tests pass. |

Remaining Phase 12 gates are tracked in `V4_API_PLAN.md`, including Release cutover, production startup, and real image ingress from both chat clients.

Validation: 43/43 focused Debug tests passed with zero skipped; v4 contract snapshots matched the 80-tool catalog. The Chrome transition-grouping and date-only clock probes passed, and the changed browser scripts compiled. `git diff --check` found no whitespace faults.
