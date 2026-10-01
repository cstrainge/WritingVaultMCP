# Incident recovery checklist

- [ ] Stop the MCP server and Microsoft Access; prevent new writes.
- [ ] Record the UTC time, observed error, last request token, and affected semantic references.
- [ ] Preserve the database, lock artifact, stderr logs, and relevant backup manifests without editing them.
- [ ] Hash the preserved database and make a working copy.
- [ ] Run schema and integrity status against the working copy.
- [ ] Verify candidate backup manifests and choose the newest valid same-database regular backup.
- [ ] Restore to a new disposable path; never overwrite the incident file.
- [ ] Run schema, integrity, representative reads, history, artificial-time, and MCP read-only smoke checks.
- [ ] Reconcile operations after the backup using request tokens and `operation_history`.
- [ ] Obtain a final verified backup of the recovered database.
- [ ] Return to service read-only, then enable writes after one reversible mutation succeeds.
- [ ] Retain the incident artifacts until the cause and recovery are reviewed.
