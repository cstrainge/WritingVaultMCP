# Release checklist

Historical v3 cutover record. Its checked items do not approve the in-progress v4 Release or production cutover; use [V4_API_PLAN.md](../../V4_API_PLAN.md) Phase 12 for that gate.

- [x] `dotnet restore` succeeds on 64-bit Windows with the supported ACE provider.
- [x] Release build succeeds with zero warnings and errors.
- [x] The explicit xUnit project run passes with zero skipped tests.
- [x] Schema checksum, migration ID, tool reference, and app configuration match the build.
- [x] Hostile schema, concurrency, rollback, protocol, recovery, and performance tests pass.
- [x] The live database has a new verified pre-cutover backup under the OneDrive root.
- [x] Live schema and integrity status are valid and row counts are recorded.
- [x] A restored disposable copy passes read-only and write-enabled smoke scenarios.
- [x] Health reports ready, no pending writes, and the latest successful regular backup.
- [x] No critical or high review findings remain.
- [x] The pre-cutover database and restore-drill artifact have an explicit retention date.

Evidence: `artifacts/PHASE10_CUTOVER_REPORT.md` and `reviews/phase10-hostile-review.md`.
