# Writing Vault documentation

The [root README](../README.md) explains the project and Debug setup. The [v4 plan](../V4_API_PLAN.md) is the progress ledger, and [design decisions](../DESIGN_DECISIONS.md) record accepted behavior. Production still uses the separate Release build; v4 development stays in Debug until an approved cutover.

| Topic | Current reference |
| --- | --- |
| Public v4 behavior | [Contract](V4_CONTRACT.md), [tool guide](V4_TOOLS.md), generated [schemas](../contracts/v4/README.md) |
| Temporary v3 fallback | [v3 tool guide](V3_TOOLS.md), generated [schemas](../contracts/v3/README.md) |
| Viewer | [Read-only web viewer](WEB_VIEWER.md) |
| Client setup | [MCP installation](MCP_CLIENT_INSTALLATION.md), [operations](OPERATIONS.md), [troubleshooting](TROUBLESHOOTING.md) |
| Storage and time | [Access runtime](ACCESS_RUNTIME.md), [migrations](SCHEMA_MIGRATIONS.md), [time and dates](TIME_AND_DATES.md), [incident recovery](INCIDENT_RECOVERY.md) |
| Requirements and evidence | [Feedback log](API_FEEDBACK_LOG.md), [fixtures](V4_TEST_FIXTURES.md), [image ingress probe](V4_IMAGE_INGRESS_PROBE.md), [tool disposition](V4_TOOL_DISPOSITION.md), [code cleanup](CODEBASE_CLEANUP_PLAN.md) |
| Release evidence format | [Manifest template](RELEASE_MANIFEST_TEMPLATE.json); it has no completed hashes and does not authorize cutover |
| Superseded design and release material | [Historical document index](archive/README.md), [older reviews](../reviews/archive/README.md) |

Generated contract JSON under `contracts/` is the exact MCP declaration. Prose guides explain intended use and must be reconciled against that generated contract before a Release build.
