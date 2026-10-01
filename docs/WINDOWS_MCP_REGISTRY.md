# Windows MCP registry

Writer's Vault serves MCP over stdio from the Release apphost at
`bin/Release/net10.0-windows/WritingVaultMcp.exe`. The Windows ODR manifest is
generated from that executable's real `initialize` and `tools/list` responses,
so its static tool declarations match the deployed v4 surface. The generator
writes `manifest.json` next to the executable; the local database and backup
paths remain in this ignored, machine-specific file.

Generate and validate the manifest without registering:

```powershell
py -3 tools/New-WritingVaultWindowsMcpManifest.py
dotnet tool install mcpb.cli --tool-path artifacts/windows-mcp/tools
artifacts/windows-mcp/tools/mcpb.exe validate bin/Release/net10.0-windows/manifest.json
```

Once Windows ODR is available, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Register-WritingVaultWindowsMcp.ps1
```

The script checks the Windows build and `odr.exe`, regenerates the manifest,
calls `odr.exe mcp add <manifest>`, and lists the registered servers. It does
not alter Claude Desktop or ChatGPT's existing connections. Regenerate and
re-register after a Release change that alters the MCP handshake or tools.

Microsoft currently documents Windows build 26220.7262 or newer for manual
registration. Its MCP bundle guide also says bundles are not accessible from
the default agent session; an MSIX-packaged server with package identity is
the supported route for that mode. Registration alone may therefore succeed
without making the Vault available to every Windows MCP host. See the
[manual registration guide](https://learn.microsoft.com/en-us/windows/ai/mcp/servers/mcp-manual)
and [MCP bundle guide](https://learn.microsoft.com/en-us/windows/ai/mcp/servers/mcp-mcpb).
