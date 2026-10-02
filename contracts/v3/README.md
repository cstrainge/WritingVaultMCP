# v3 compatibility snapshot

`tool-schemas.json` is captured from both read-only and read-write v3 MCP connections backed by a newly created, migrated disposable Access database. It freezes the compatibility contract while v4 is developed.

The normal test compares the live declarations with this file. Intentional v3 contract changes require a one-time Debug test run with `UPDATE_CONTRACT_SNAPSHOTS=1`, followed by review of the resulting diff.

The Phase 12 snapshot adds only the `KnownRange` and `UncertainRange` story-date enum values. Existing v3 fields and operations are unchanged; the extra values reflect the additive date-meaning migration shared with v4.

The project-event release also adds the optional `birthdayRecurring` character flag to create and patch requests. Existing clients can omit it.
