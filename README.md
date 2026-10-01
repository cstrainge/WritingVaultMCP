# Writing Vault MCP

Writing Vault MCP is a local, single-user MCP server backed by a Microsoft Access `.accdb` file. It stores continuity-isolated fictional canon, fuzzy story dates, artificial story time, provenance, bidirectional character relationships, co-ownership, history, and recoverable soft deletion.

The root server project owns the named-pipe backend, Access adapter, and MCP tools. `WritingVault.Client` is the typed read-only MCP client, and `WritingVault.Web` serves the loopback viewer through that client. The web project has no database writer or direct Access dependency. `contracts/v3` and `contracts/v4` hold generated public declarations; `tests/WritingVaultMcp.Tests` creates disposable Access databases for behavior and protocol checks.

Documentation: [v4 tool guide](docs/V4_TOOLS.md), [client installation](docs/MCP_CLIENT_INSTALLATION.md), [read-only web viewer](docs/WEB_VIEWER.md), and [v4 implementation plan](V4_API_PLAN.md).

## Code map

| Area | Responsibility |
| --- | --- |
| `Program.cs`, `Mcp/VaultProcessHost.cs` | CLI modes, process election, current-user named-pipe backend, and per-client MCP adapters. |
| `Mcp/V4/`, `Application/` | Public v4 requests/results, semantic name and reference resolution, validation, and application commands. Generated declarations in `contracts/v4/` are the public contract. |
| `Infrastructure/Access/` | Access reads and writes, one write coordinator, schema migrations, integrity checks, backup, and companion image storage. The `AccessV4ReadService.*` files divide search, record, timeline, and history reads. |
| `WritingVault.Client/` | Typed, read-only MCP client used by the viewer. It does not open Access. |
| `WritingVault.Web/` | Loopback HTTP host, isolated tab sessions, read-only routes, safe record rendering, and graphical timeline. |
| `tests/WritingVaultMcp.Tests/`, `tools/` | Disposable Access fixtures, behavior/protocol tests, local diagnostic probes, and Windows launch helpers. |

The v3 MCP surface remains available for the deployed compatibility period. Change a storage invariant in the Access layer and its tests; change a public v4 request in `Mcp/V4/`, regenerate `contracts/v4/`, then update the typed client and viewer as needed. All local tests use disposable databases; the production `.accdb`, backup root, downloaded tunnel client, and credentials are outside the repository.

Development status on 2026-09-29: v4 and the read-only viewer are being validated in **Debug only**. The separately deployed Release artifact remains available to other clients and must not be rebuilt or replaced until explicit Phase 12 cutover approval. The Release path below is an example of the future client configuration, not an instruction to build or switch clients now.

## Requirements

- 64-bit Windows
- .NET 10 SDK/runtime
- 64-bit Microsoft Access Database Engine with `Microsoft.ACE.OLEDB.12.0`
- A local `.accdb` file and a writable containing directory (ACE creates `.laccdb` lock files)

Set the local database and backup paths in the ignored `appsettings.json`. Production backups and cached source snapshots belong under the configured OneDrive-backed backup root; no live database or local backup belongs in the source repository.

## Build and verify

During v4 development, build and test **Debug** only. The deployed Release artifact is in use by other clients and is not a development target:

```powershell
dotnet restore .\WritingVault.slnx
dotnet build .\WritingVault.slnx --configuration Debug --no-restore
dotnet test .\WritingVault.slnx --configuration Debug --no-restore
```

The solution includes the server, typed client, web viewer, and test project. Name `WritingVault.slnx` in build and test commands; an implicit root target is ambiguous. Record the actual test count from each run rather than reusing an older release total. Release building and production cutover are Phase 12 gates.

## Configure

Copy `appsettings.example.json` to the ignored `appsettings.json`, then replace the example paths with local paths:

```json
{
  "WritingVault": {
    "DatabasePath": "C:\\path\\to\\WritingVault.accdb",
    "Provider": "Microsoft.ACE.OLEDB.12.0",
    "BackupRoot": "C:\\path\\to\\WritingVaultBackup",
    "BackupRetentionCount": 20,
    "ReadOnly": false
  }
}
```

The adapter accepts `--database`, `--provider`, `--backup-root`, `--client-label`, and `--read-only` overrides. When both database and backup root are supplied explicitly, a local `appsettings.json` is optional; this is how clean-checkout tests use disposable databases. The elected backend rejects missing files, non-`.accdb` paths, schema drift, failed integrity checks, and databases beyond the supported size ceiling.

Database paths must resolve to a local filesystem. UNC paths and mapped network drives are rejected. `serve --read-only` is the inspection mode, plain `serve` is the normal write-enabled mode, and schema/backup/purge commands form the separate administrative mode.

## Initialize or verify a database

```powershell
dotnet run -- schema init --database C:\path\to\new.accdb
dotnet run -- schema status --database C:\path\to\new.accdb
dotnet run -- integrity status --database C:\path\to\new.accdb
```

`schema init` never overwrites a file. Migrations are administrative commands and never run when serving MCP requests.

## Run and connect

```powershell
dotnet run -- serve
dotnet run -- serve --read-only
```

Example MCP client configuration after a Release build:

```json
{
  "mcpServers": {
    "writing-vault": {
      "command": "dotnet",
      "args": [
        "C:\\path\\to\\WritingVaultMCP\\bin\\Release\\net10.0-windows\\WritingVaultMcp.dll",
        "serve"
      ]
    }
  }
}
```

Each client sees local stdio. Adapters for the same database share one current-user named-pipe backend, so Claude and ChatGPT can run concurrently with independent continuity and artificial-time state. Continuities are selected by name; all other MCP records use semantic references, and internal database IDs and operation GUIDs remain hidden. Read-only mode omits every mutation tool. Migration and permanent purge are CLI-only administrative operations.

See the [documentation index](docs/README.md), [v4 API and read-only web plan](V4_API_PLAN.md), [codebase cleanup plan](docs/CODEBASE_CLEANUP_PLAN.md), [client installation](docs/MCP_CLIENT_INSTALLATION.md), [read-only web viewer](docs/WEB_VIEWER.md), [v4 tools](docs/V4_TOOLS.md), [v3 compatibility reference](docs/V3_TOOLS.md), [API feedback log](docs/API_FEEDBACK_LOG.md), [time semantics](docs/TIME_AND_DATES.md), [operations](docs/OPERATIONS.md), and [troubleshooting](docs/TROUBLESHOOTING.md).
