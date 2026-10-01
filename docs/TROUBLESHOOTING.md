# Troubleshooting

## ACE provider missing or architecture mismatch

Use 64-bit PowerShell and run `dotnet --info`. Install the 64-bit Microsoft Access Database Engine and confirm `Microsoft.ACE.OLEDB.12.0` is registered. The project is pinned to x64 and refuses a 32-bit process.

## Database locked or second owner

Close Microsoft Access and stop other Writing Vault processes. Use Task Manager or Resource Monitor to find remaining `dotnet`, `MSACCESS`, or automation processes. A sibling `.laccdb` can briefly remain while ACE releases handles. Do not delete it until no owning process exists. Administrative CLI backup and migration deliberately refuse an active lock. The v4 MCP backup tool instead coordinates a live snapshot through the elected backend.

## `schema.not_ready` or startup schema failure

Run:

```powershell
dotnet run -- schema status
dotnet run -- integrity status
```

Do not edit the database to silence individual findings. Preserve the file and its report. For an empty interrupted baseline, follow `SCHEMA_MIGRATIONS.md`; for populated data, restore or develop a reviewed forward migration.

## Retryable storage failure

A lock/share failure returns `storage.failure` with `retryable: true` after bounded retries. Retry with the same request token and identical input so a commit whose response was lost cannot duplicate data. Permanent validation, duplicate, reference, and concurrency failures require corrected input or a fresh read.

## Corruption or failed integrity verification

Stop writes, copy the suspect file without modifying it, preserve logs, and follow `INCIDENT_RECOVERY.md`. Restore only from a manifest that passes hash, schema, and integrity checks. Compact/repair a copy, never the sole original.

## Server will not start after a crash

Confirm whether a Writing Vault backend already owns the database lease. A new stdio adapter should attach to that backend automatically; administrative CLI backup, migration, restore, and purge still require every adapter and the backend to stop. If no owner exists but an Access lock remains, preserve the database and lock artifacts before cleanup. Run schema and integrity status before returning to write mode.
