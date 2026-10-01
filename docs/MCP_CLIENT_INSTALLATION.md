# Installing Writing Vault MCP in Claude Desktop and ChatGPT

Reviewed: 2026-09-29. All `C:\path\to\...` paths below are examples to replace with local paths. The deployed Release artifact still serves the v3 compatibility surface; its 2026-09-28 test and migration results are historical evidence, not a v4 release gate. The v4 source and Debug artifacts are under validation. Do not rebuild or replace Release until cutover approval. The v4 client commands below apply after that cutover; existing clients should stay attached to Release meanwhile.

Writing Vault MCP is a Windows-only, local MCP server using the stdio transport. Claude Desktop and the local MCP host in the ChatGPT desktop app can launch it directly. ChatGPT web uses a separately registered app: Secure MCP Tunnel connects this private stdio server without exposing it publicly. The two ChatGPT paths have different setup and session state.

## Before installing

The machine must have:

- 64-bit Windows;
- the 64-bit .NET 10 runtime;
- the 64-bit Microsoft Access Database Engine providing `Microsoft.ACE.OLEDB.12.0`;
- this repository at `C:\path\to\WritingVaultMCP`;
- an initialized database whose schema matches this build.

Build and test the current source in Debug from PowerShell:

```powershell
Set-Location -LiteralPath C:\path\to\WritingVaultMCP
dotnet restore .\WritingVault.slnx
dotnet build .\WritingVault.slnx --configuration Debug --no-restore
dotnet test .\WritingVault.slnx --configuration Debug --no-restore --no-build
```

During an approved Phase 12 cutover, run the equivalent Release build and test gate. The production executable is then:

```text
C:\path\to\WritingVaultMCP\bin\Release\net10.0-windows\WritingVaultMcp.dll
```

Confirm that the artifact exists before editing either client configuration:

```powershell
Test-Path -LiteralPath C:\path\to\WritingVaultMCP\bin\Release\net10.0-windows\WritingVaultMcp.dll
```

The result must be `True`.

The production database is:

```text
C:\path\to\WritingVault.accdb
```

The live database completed migration, Release smoke testing, and a verified backup/restore drill on 2026-09-28. For disposable usability testing, use:

```text
C:\path\to\WritingVaultMCP\usability\WritingVault.Usability.accdb
```

Any number of local stdio adapters for the same normalized database path hand off to one current-user backend automatically. Claude, ChatGPT desktop, and the OpenAI tunnel may therefore run together. Stop every adapter and wait for the short idle shutdown before backup, migration, restore, or purge work.

## Claude Desktop

Claude Desktop can start Writing Vault MCP as a child process over stdio. The MCP SDK documentation identifies the Windows configuration file as `%APPDATA%\Claude\claude_desktop_config.json`; absolute executable and server paths avoid dependency on Claude's working directory.

### 1. Install and initialize Claude Desktop

Install Claude Desktop, open it once, then fully quit it. Opening it once creates its application-data directory.

### 2. Open the configuration file

In PowerShell:

```powershell
notepad "$env:APPDATA\Claude\claude_desktop_config.json"
```

If the file does not exist, create it. If it already contains `mcpServers`, merge the `writing-vault` entry into that object rather than replacing other servers.

### 3. Add Writing Vault MCP

For the production database:

```json
{
  "mcpServers": {
    "writing-vault": {
      "command": "C:\\Program Files\\dotnet\\dotnet.exe",
      "args": [
        "C:\\path\\to\\WritingVaultMCP\\bin\\Release\\net10.0-windows\\WritingVaultMcp.dll",
        "serve",
        "--database",
        "C:\\path\\to\\WritingVault.accdb",
        "--client-label",
        "Claude Desktop",
        "--tool-surface",
        "v4"
      ]
    }
  }
}
```

For intentional disposable testing, change only the database argument to:

```json
"C:\\path\\to\\WritingVaultMCP\\usability\\WritingVault.Usability.accdb"
```

To expose read tools only, append this argument:

```json
"--read-only"
```

The end of `args` would then look like:

```json
[
  "--database",
  "C:\\path\\to\\WritingVault.accdb",
  "--read-only"
]
```

### 4. Restart and verify

Fully quit Claude Desktop, including its notification-area process, and reopen it. In a chat, select **+ → Connectors** to confirm that `writing-vault` is connected and inspect its tools. Anthropic also exposes connection status and server logs in Claude Desktop's developer settings.

Suggested smoke prompts:

```text
Use writing-vault to check vault health.
```

```text
List the continuities in the vault without changing anything.
```

Use an explicit mutation prompt only when you intend to change the production vault. Confirmed writes from Claude and ChatGPT are serialized through the same backend.

### Claude troubleshooting

- Confirm the Release DLL exists at the configured path.
- Confirm `C:\Program Files\dotnet\dotnet.exe` exists.
- Validate the JSON; a trailing comma makes the configuration invalid.
- Fully quit and restart Claude after every configuration change.
- Inspect `%APPDATA%\Claude\logs\mcp.log` and the matching `mcp-server-writing-vault.log`.
- Multiple adapters attach to one elected backend automatically. For administrative commands, close every adapter and wait for backend idle shutdown. Do not delete an active `.laccdb` file.
- Run schema and integrity diagnostics manually:

```powershell
dotnet C:\path\to\WritingVaultMCP\bin\Release\net10.0-windows\WritingVaultMcp.dll `
  schema status --database C:\path\to\WritingVault.accdb

dotnet C:\path\to\WritingVaultMCP\bin\Release\net10.0-windows\WritingVaultMcp.dll `
  integrity status --database C:\path\to\WritingVault.accdb
```

Official references: [Anthropic local MCP/desktop extension guidance](https://support.claude.com/en/articles/10949351-getting-started-with-local-mcp-servers-on-claude-desktop) and [MCP SDK host configuration](https://py.sdk.modelcontextprotocol.io/get-started/real-host/).

## ChatGPT desktop: direct local MCP

The [official ChatGPT desktop MCP guide](https://learn.chatgpt.com/docs/extend/mcp) documents local STDIO servers under **Settings → MCP servers → Add server**. Choose STDIO, name the server `writing-vault`, and enter the executable and arguments for the Release DLL. Restart the app after saving, then type `/mcp` in the composer to inspect the connection. This local MCP configuration is shared with the Codex CLI and IDE extension; ChatGPT web does not read it.

After the approved v4 cutover, the equivalent `config.toml` entry is:

```toml
[mcp_servers.writing-vault]
command = "C:\\Program Files\\dotnet\\dotnet.exe"
args = [
  "C:\\path\\to\\WritingVaultMCP\\bin\\Release\\net10.0-windows\\WritingVaultMcp.dll",
  "serve", "--database", "C:\\path\\to\\WritingVault.accdb",
  "--client-label", "ChatGPT Desktop", "--tool-surface", "v4"
]
```

The default personal configuration file is `%USERPROFILE%\.codex\config.toml`; the desktop app's Settings UI is simpler if this is your first local server. Until cutover, keep the current Release DLL and its v3 surface: omit `--tool-surface v4`. Add `--read-only` only if you want a read-only local connection. This desktop adapter and Claude's adapter share the elected backend but maintain independent continuity and story-time selections.

## ChatGPT web: Secure MCP Tunnel

The [official Secure MCP Tunnel guide](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) documents the private-server path used by ChatGPT web plugins. Keep the tunnel if you use ChatGPT web or a hosted plugin; direct desktop STDIO does not replace that connection.

Direct setup links:

- [Create or manage an OpenAI MCP tunnel](https://platform.openai.com/settings/organization/tunnels)
- [Download the latest official `tunnel-client`](https://github.com/openai/tunnel-client/releases/latest)
- [Open ChatGPT Plugins](https://chatgpt.com/plugins)

### Supported private/local setup with Secure MCP Tunnel

This route keeps the Access database and stdio process on this computer. The tunnel client makes an outbound HTTPS connection to OpenAI; it does not require opening an inbound firewall port.

You need:

- a ChatGPT plan/workspace with developer-mode access appropriate to the tool permissions you need;
- access to an OpenAI Platform organization;
- Tunnels **Read + Use** permissions, plus **Manage** if creating the tunnel;
- a tunnel ID from OpenAI Platform tunnel settings;
- a runtime API key for `tunnel-client`.

#### 1. Create the tunnel

Open [OpenAI Platform tunnel settings](https://platform.openai.com/settings/organization/tunnels), create a tunnel associated with the intended ChatGPT workspace, and record its `tunnel_id`. Download the current Windows client from the link supplied there or from the [latest official `tunnel-client` release](https://github.com/openai/tunnel-client/releases/latest). Keep the executable and all notices supplied with the download together in the ignored local `mcp-tunnel/` directory. The current local distribution includes `LICENSE`, `NOTICE`, a versioned `*-licenses.txt`, and an SPDX inventory; retain those files when updating the client. Neither the executable nor its local profile belongs in the source repository.

#### 2. Configure the local stdio command

After placing the separately downloaded tunnel client in the project directory and completing the approved Release cutover, double-click `Run-WritingVault-Tunnel.bat` in the project root. The first run securely prompts for the runtime API key and tunnel ID. Each run refreshes the local profile for the current project path, validates the setup, and starts Release against the database configured on that machine. The downloaded executable and its profile are local artifacts, not repository contents.

The launcher protects the runtime API key with Windows Data Protection API encryption scoped to the current Windows user and stores the encrypted value at `%LOCALAPPDATA%\WritingVaultMCP\openai-tunnel-api-key.dpapi`. Later runs decrypt it only into the launcher process environment, then clear that environment variable when the launcher exits. The plaintext key is never written to the repository or tunnel profile. To delete the saved key and prompt for a replacement, run:

```powershell
.\Run-WritingVault-Tunnel.bat -ForgetApiKey
```

The commands below show the equivalent manual setup.

In PowerShell, from the directory containing `tunnel-client.exe`:

```powershell
$env:CONTROL_PLANE_API_KEY = "<your key>"

.\tunnel-client.exe init `
  --sample sample_mcp_stdio_local `
  --profile writing-vault `
  --tunnel-id tunnel_REPLACE_ID `
  --mcp-command 'dotnet "C:/path/to/WritingVaultMCP/bin/Release/net10.0-windows/WritingVaultMcp.dll" serve --database "C:/path/to/WritingVault.accdb" --client-label "ChatGPT Tunnel" --tool-surface v4'
```

Replace the example tunnel ID and API key. Keep forward slashes in the MCP command paths because the tunnel client's shell-style command parser treats backslashes as escape characters. This command targets the production vault; use the earlier usability-database example only when you intentionally want disposable testing.

For a read-only tunnel, add `--read-only` inside the `--mcp-command` value.

#### 3. Diagnose and run the tunnel

```powershell
.\tunnel-client.exe doctor --profile writing-vault --explain
.\tunnel-client.exe run --profile writing-vault
```

Keep that process running while ChatGPT scans or calls tools. The tunnel client's loopback admin UI and `/healthz`/`readyz` endpoints can be used for local diagnostics as described in the OpenAI documentation.

After one normal interactive launch has created the profile and saved the DPAPI-protected key, the same launcher can run without prompts or an admin window:

```powershell
.\Run-WritingVault-Tunnel.bat -Background
```

Background mode requires the Release DLL, the `writing-vault` tunnel profile, and the saved key at `%LOCALAPPDATA%\WritingVaultMCP\openai-tunnel-api-key.dpapi`. It always decrypts that saved key for the current Windows user, never runs `init` or `doctor`, never opens the tunnel admin UI, and retains the single-profile mutex for the lifetime of the tunnel. Phase 8 installs this command as a hidden current-user Scheduled Task; do not create a competing startup entry manually.

| Exit | Meaning |
|---:|---|
| `0` | Normal exit, or the same profile is already running |
| `10` | Tunnel client executable is missing |
| `11` | Release server DLL is missing |
| `12` | Production database is missing |
| `20` | Saved tunnel profile or tunnel ID is missing |
| `21` | Saved DPAPI credential is missing, empty, or cannot be decrypted by this user |
| `22` | Launcher arguments or tunnel ID are invalid |
| `30` | Interactive profile initialization failed |
| `31` | Interactive tunnel diagnostics failed |
| `40` | The tunnel process exited with an error |
| `1` | Unexpected launcher failure |

Run only one `writing-vault` tunnel-client instance on this computer. Two instances using the same tunnel ID create two independent MCP adapter sessions, so connection-local continuity and artificial-time selections can appear to switch between calls. `Run-WritingVault-Tunnel.bat` detects an existing profile process and also holds a named mutex to prevent launch races. Claude Desktop and ChatGPT desktop use their own adapters and may run at the same time.

Do not put the runtime API key into this repository, `appsettings.json`, the Access database, or screenshots. Use the launcher's Windows-encrypted credential file instead.

#### 4. Create the ChatGPT app

The exact menu depends on plan and workspace policy:

1. Have the workspace administrator grant developer-mode access.
2. Enable developer mode in ChatGPT settings. The current [official plugin setup guide](https://developers.openai.com/plugins/build/plugins) places this under **Settings → Security and login**; workspace policy may require an administrator to enable access first.
3. In ChatGPT web, open [ChatGPT Plugins](https://chatgpt.com/plugins) and create a developer-mode app.
4. Choose **Tunnel** as the connection type.
5. Select the tunnel or enter its `tunnel_id`.
6. Scan tools and review every discovered read/write action.
7. Create the draft app and enable it for your account.
8. Open a new chat, select or mention the app, and call `vault_health` first.

Write operations may require confirmation. The tunnel launcher now targets the production vault, so treat confirmed mutations as real changes.

#### 5. Refresh after tool changes

ChatGPT retains a reviewed snapshot of an app's tools and inputs. After changing Writing Vault's MCP tool surface:

1. Keep `Run-WritingVault-Tunnel.bat` running.
2. Open [ChatGPT Plugins](https://chatgpt.com/plugins).
3. Open the Writer's Vault MCP connection and select **Refresh**.
4. Before v4 cutover, confirm the v3 metadata contains `session_continuity_set` with `continuityName`, and that `entity_search` has no `continuityId` while `entity_get` uses `entityReference` rather than `entityId`. After cutover, confirm the v4 `session_set`, `search`, `get`, and `timeline_get` declarations instead.
5. Confirm the advertised tool count matches the selected surface (68 for v3 or 70 for the current Debug v4 contract), review the read/write diff, and save the connection.
6. Start a new conversation before retesting; an existing conversation may retain the previous action snapshot.

The v3 continuity workflow is `continuity_list` followed by `session_continuity_set`. There is intentionally no `continuity_select` tool name. Once selected, the continuity is implicit for later calls on that connection.

### Public HTTPS alternative

ChatGPT can also connect to a publicly reachable HTTPS MCP endpoint. Writing Vault currently implements stdio only, so this alternative requires additional server work: a Streamable HTTP transport, authentication, TLS, deployment, and a stable `/mcp` URL. Do not point an internet tunnel such as ngrok directly at the present stdio executable; there is no HTTP listener to forward.

## Using Claude and ChatGPT together

The released build allows Claude, ChatGPT desktop, and the ChatGPT web tunnel to run simultaneously against the same database. Each integration starts a local stdio adapter; all adapters attach to one elected backend, which owns Access and serializes writes. Each connection keeps its own selected continuity and artificial time. When the last adapter disconnects, the backend drains in-flight work and exits after a short grace period.
