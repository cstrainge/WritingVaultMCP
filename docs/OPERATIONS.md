# Operations

## Ownership and locking

One elected local backend owns each database and serializes writes. Multiple stdio adapters may share that backend, with independent continuity and artificial-time session state. Direct Microsoft Access edits and multi-host/network-share writes are outside the supported boundary because they can bypass application-only continuity, cycle, overlap, and co-owner rules. Stop every adapter, wait for the backend's idle shutdown, and close Access before administrative CLI backup, restore, migration, or compact/repair. The v4 MCP backup tool has its own coordinated live path. Never delete a lock file while a process may still own it.

The database and backup root must be on local filesystems. The server rejects UNC database paths, mapped network drives, missing files, and extensions other than `.accdb`. The Windows account running the backend needs read/write/create/delete permission on the database directory because ACE creates a sibling lock file. It also needs read/write/create permission on the backup root. Restrict both directories to the single user account; Writing Vault does not add accounts or ACLs of its own.

Operational modes are explicit:

- `serve --read-only` exposes reads plus connection-local session controls.
- `serve` exposes the normal read/write semantic API.
- `schema`, `integrity`, `backup`, and `purge` are administrative CLI commands and are never MCP tools.

The backend binds only to a current-user Windows named pipe. Remote or multi-host operation requires a separately designed authenticated HTTPS deployment; do not expose the pipe or Access file through a network share.

## Viewer, tunnel, and tray startup

The Phase 12 startup controller supports two isolated configurations. `Configure-WritingVault-Startup.bat status Release` inspects the production tasks; `Configure-WritingVault-Startup.bat status Debug` inspects the development tasks. `plan` prints the exact task actions without installing them. After the matching build and tunnel profile are ready, `install`, `start`, `stop`, `restart`, and `remove` take the same second argument. Omitting it means Release. The tray's menu actions carry their build configuration automatically.

Release uses the configured production database, viewer URL `http://127.0.0.1:5284`, tunnel profile `writing-vault`, and tasks named `Writing Vault Viewer`, `Writing Vault ChatGPT Tunnel`, and `Writing Vault Tray`. Debug uses `usability\WritingVault.Usability.accdb`, URL `http://127.0.0.1:5285`, profile `writing-vault-debug`, and the corresponding `Writing Vault Debug ...` tasks. Debug backups remain under the configured OneDrive backup root in its `Debug` subfolder. Their PID, health, log, and Windows-encrypted tunnel credential files are separate. The Debug tunnel needs its own tunnel ID; the startup installer rejects a Debug profile that targets the Release server, database, or tunnel ID. The tray never stores a tunnel credential.

`status` and `plan` are read-only. Install tasks with `Configure-WritingVault-Startup.bat install Release`, then start the viewer and tunnel with `start Release` and the tray with `start-tray Release`. The three tasks recover independently and use the current Windows user's interactive logon. Do not use `install` against a live preview or production listener on the selected fixed port; installation reports that port as occupied. Task ownership compares Windows SIDs because Task Scheduler may shorten a local account name when registering it.

## Backup

The canonical root is the configured OneDrive-backed `BackupRoot`, ordinarily the current user's `Documents\WritingValutBackup` folder. Administrative CLI backup requires a closed source, holds the owner lease and a stable read handle while hashing and copying, verifies SHA-256, opens the copy, and runs schema and integrity verification. The v4 MCP `vault_backup_create` operation instead runs through the elected backend: it briefly quiesces writes, includes referenced companion image assets, verifies the resulting backup set, and returns path-free metadata. It is idempotent by mutation token; reusing a token after retention has removed its backup fails and requires a new token. The manifest records provider, purpose, schema identity, and retention configuration. Retention defaults to 20 and never accepts fewer than two.

```powershell
dotnet run -- backup create --purpose regular
dotnet run -- backup create --purpose regular --retention 20
dotnet run -- backup verify --manifest C:\path\to\WritingVault.<timestamp>.manifest.json
```

Pre-migration backups allow an older schema but still require a readable, hashed copy:

```powershell
dotnet run -- backup create --purpose pre-migration
dotnet run -- backup verify --manifest C:\path\to\WritingVault.<timestamp>.manifest.json --allow-incompatible-schema
```

## Restore

Restore always targets a new path and never overwrites an existing file:

```powershell
dotnet run -- backup restore --manifest C:\path\to\manifest.json --target C:\restore\WritingVault.restored.accdb
dotnet run -- schema status --database C:\restore\WritingVault.restored.accdb
dotnet run -- integrity status --database C:\restore\WritingVault.restored.accdb
dotnet run -- serve --database C:\restore\WritingVault.restored.accdb --read-only
```

After verification, stop all owners, retain the damaged file, and move the restored copy into service using normal filesystem controls. An older pre-migration backup requires `--allow-incompatible-schema` and must be migrated before serving.

## Migration

Use `schema status` first. Ordinary v3-to-v4 migration is an additive forward migration on a closed database, after a verified pre-migration backup:

```powershell
dotnet run -- schema migrate
```

The legacy v2 baseline can rebuild only an empty database and requires a matching pre-migration manifest:

```powershell
dotnet run -- schema migrate --allow-empty-rebuild --backup-manifest C:\path\to\manifest.json
```

That exceptional rebuild command refuses changed source content, a foreign manifest, lock files, and any user data. See [schema lifecycle](SCHEMA_MIGRATIONS.md) for the current ledger and interrupted-migration recovery.

## Permanent purge

Normal deletion is reversible. Permanent entity purge requires a soft-deleted entity, its exact version, a short-lived preview token, and a recent verified regular backup from the same database path:

```powershell
dotnet run -- purge preview --entity 123 --expected-version 4
dotnet run -- backup create --purpose regular
dotnet run -- purge execute --token <token> --backup-manifest C:\path\to\manifest.json
```

Purge is replay-safe and journals the operation. Execute also requires the current closed database hash to equal the backup's recorded source hash; any write after backup creation invalidates the evidence and requires a new backup. Purge is intentionally absent from MCP tools.

## Compact and repair

Create and verify a regular backup, stop all database owners, confirm the lock file is absent, then use Access **Database Tools > Compact and Repair Database** on a disposable copy first. Run schema and integrity status afterward. Replace the production file only after both pass and retain the pre-maintenance backup.

The supported ceiling is 1.5 GiB; startup warns above 1.2 GiB. Archive old research snapshots or migrate to a server database when growth, multiple concurrent writers, remote access, or workloads near the Access 2 GiB format ceiling become routine.

## Release provenance

Before a Phase 12 Release build, review and commit the exact source tree. Run
`tools/New-WritingVaultReleaseManifest.ps1` only from that clean checkout. It
refuses a missing commit or any tracked or untracked source change before
building, then writes an unverified candidate manifest under the ignored
`artifacts` directory. The manifest records the source commit, schema migration,
named entry-point hashes, and SHA-256 hashes for every file in the server,
client, viewer, and tray Release output directories. A successful manifest
build is provenance evidence; it does not approve production cutover or replace
the Release tests, backup, recovery, and hostile review gates.

## Logs


Logs are one JSON object per line on stderr so stdio MCP frames remain clean. Events cover adapter/backend lifecycle, client connections, schema and integrity verification, reads, mutation queue/commit/replay/rollback/retry, administrative commands, provider failures, and size warnings. Backend diagnostics may include internal operation IDs; MCP clients use readable request tokens and never receive those GUIDs. Log fields never include request bodies, full story content, SQL, connection strings, or database paths. Journal summaries redact configured long-form values to length and SHA-256 metadata while the vault records and backups retain the content. Apply the host's normal log rotation; retain operational logs for 30 days unless an incident requires longer preservation.
