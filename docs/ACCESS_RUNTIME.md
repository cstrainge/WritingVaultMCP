# Supported Access Runtime

Status: v3 production runtime verified in the earlier release cycle; v4 storage is under Debug validation  
Platform boundary: Local 64-bit Windows process

## Supported configuration

- Application target: `.NET 10` on Windows x64.
- MCP SDK: [official `ModelContextProtocol` package](https://www.nuget.org/packages/ModelContextProtocol/2.2.0) pinned to `2.2.0`; stdio-client and arbitrary-stream server transports have protocol coverage. The existing Release results apply to v3, while v4 validation remains in Debug.
- Database format: Access `.accdb`.
- Configured provider: `Microsoft.ACE.OLEDB.12.0`.
- Required provider architecture: x64, matching the MCP process.
- Database location: local filesystem; UNC paths, mapped network drives, and multi-host writes are rejected.
- Write ownership: one elected backend process per physical local-volume path; drive letters and local volume mount-point aliases resolve to the same backend identity, and any number of local stdio adapters may share it.

The project is `net10.0-windows` with `PlatformTarget=x64`, matching the supported ACE provider architecture.

All application SQL runs through `AccessCommand`, which applies a 30-second command timeout and positional parameters. Schema DDL is confined to the explicit migrator. Migration `20260927_003_longtext_checks` removes unsafe `Len`/`Trim` checks from required Long Text columns because ACE fails those expressions on large values; `NOT NULL` remains in the table definition and application validation rejects blank input.

## Verified development environment

```text
Windows version:       10.0.26200
OS architecture:      x64
.NET SDK:             10.0.401
.NET host/runtime:    10.0.12 x64
ACE providers found:  Microsoft.ACE.OLEDB.12.0
                      Microsoft.ACE.OLEDB.16.0
```

ACE 12.0 is retained as the configured compatibility provider because it is installed and successfully opened the live-format database, backups, and disposable integration databases. ACE 16.0 being installed does not change the configured provider automatically.

## Lock-file expectations

- ACE normally creates a sibling `.laccdb` while a database connection is open.
- The containing directory must therefore be writable even for operations that are logically reads.
- A lock file is not by itself proof that Microsoft Access is visibly open; another ACE connection may own it.
- Every repository and administrative operation must dispose readers, commands, transactions, and connections deterministically.
- Tests must confirm their temporary `.laccdb` disappears.
- Administrative closed-source backups, restore, and schema replacement require exclusive access and refuse to proceed while the source lock file exists. The v4 `vault_backup_create` MCP operation is a separate coordinated path: the elected backend briefly quiesces writes, snapshots through a stable read handle, verifies the database and companion assets, then resumes queued writes.

## Schema snapshot command

The local PowerShell policy blocks unsigned scripts by default. Run the repository exporter with a process-only bypass; this does not change machine or user policy:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Export-AccessSchema.ps1 `
  -DatabasePath <path-to-accdb> `
  -OutputPath .\artifacts\schema.snapshot.json
```

## Runtime startup checks

The MCP host checks:

1. The process is 64-bit.
2. The configured ACE provider can be instantiated.
3. The database path is an existing local `.accdb` file.
4. The containing directory allows ACE lock-file creation when writes are enabled.
5. The schema version and manifest match the application.
6. The adapter can connect to the elected current-user backend, starting it when none exists.

Failures return actionable local diagnostics while client-facing MCP errors omit connection strings and filesystem paths.
