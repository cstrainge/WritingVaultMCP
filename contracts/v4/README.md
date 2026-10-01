# Generated v4 contract snapshots

These files are generated from `Mcp/V4/V4ContractCatalog.cs` and checked in for review and compatibility tests.

Regenerate them from a Debug build:

```powershell
dotnet build .\WritingVault.slnx --configuration Debug --no-restore
dotnet .\bin\Debug\net10.0-windows\WritingVaultMcp.dll contract export --output .\contracts\v4
```

- `tool-schemas.json`: every public v4 tool, access classification, input schema, and output schema.
- `common-schemas.json`: reusable reference, paging, date, error, mutation, clock, timeline, and change shapes.
- `examples.json`: a generated request/response pair for every tool.

The Markdown record/image link grammar, version pinning, and current implementation status are documented in [`docs/V4_CONTRACT.md`](../../docs/V4_CONTRACT.md#markdown-links-in-notes). The note-writing tool descriptions in `tool-schemas.json` also carry a short syntax and status reminder.

Do not hand-edit generated JSON. Contract tests fail when the generator and snapshots differ.
