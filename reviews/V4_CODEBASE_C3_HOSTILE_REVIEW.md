# C3 hostile review: clean checkout and repository staging

Status: **zero open C3 findings** on 2026-09-29. The verified artifacts are Debug-only and use disposable Access databases. No commit, Release build, or production cutover was made.

## Findings resolved

1. A staged checkout normalized generated JSON to LF, while Windows snapshot tests compared it byte-for-byte with CRLF serialization. Both v3 and v4 contract tests now compare exact content after line-ending normalization. The v3 and v4 focused contract gate passed 9/9 from the checkout.
2. Active documentation referred to a Windows startup page that did not exist and did not distinguish the live MCP backup from closed-source administrative backup. C2 corrected those pages before the final staged set was reviewed.
3. Initial staged whitespace checks reported intentional Markdown hard-break spaces and two source files with extra EOF blank lines. The source files were cleaned, Markdown whitespace policy was declared in `.gitattributes`, and `git diff --cached --check` now returns zero findings.

## Clean-checkout rehearsal

- A disposable checkout was materialized from the prospective Git index under ignored `artifacts/clean-checkout-c3-20260929`. A normal user environment can use the README's `dotnet restore`; this restricted sandbox required an explicit local NuGet config and unsandboxed .NET access to its user-profile Windows SDK directory. These sandbox-only files remain under ignored `artifacts/`.
- The four-project solution restored and built in Debug with **zero warnings and errors**. The full Debug suite passed **179/179** after the line-ending fix. Subsequent edits affected documentation, Git attributes, and two trailing blank lines; the refreshed staged checkout built again with zero warnings and errors.
- `contract export` from that Debug binary reported surface `4.0` and 70 tools. All three generated JSON artifacts matched the checked-in v4 snapshots after line-ending normalization.
- The Debug viewer launched at its literal `http://127.0.0.1:5284` URL against the checkout's generated Lostville preview database. The root returned HTTP 200 with the strict content-security policy; `/api/bootstrap` returned one continuity and `ready: true`. The exact test viewer process was stopped and the port closed afterward.

## Staging audit

- The disposable and real root Git indexes contained the same **198** entries and identical blob hashes before this review file was added. The final checked C3 documentation and review are staged as part of this same source tree; no commit was created.
- No staged path entered `artifacts/`, `usability/`, `bin/`, `obj/`, `.tunnel-client/`, or `mcp-tunnel/`. There was no `.accdb`, lock file, executable, library binary, local `appsettings.json`, log, PID file, or file containing a NUL byte. No staged file exceeded 1 MiB.
- Staged-content scans found no personal absolute path, long tunnel identifier, common API-key shape, or private-key block. The retained Lostville examples in fixtures and the temporal requirements are deliberate functional cases; private feedback, timeline drafts, databases, and operational profiles remain ignored.

No C3 issue remains open. The Phase 12 same-commit release manifest and production staging review remain separate gates.
