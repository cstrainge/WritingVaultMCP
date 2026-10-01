"""Generate an ODR/MCPB manifest from the deployed Release server's wire metadata.

The output lives beside WritingVaultMcp.exe so MCPB's ${__dirname} resolves to
the actual Release apphost and its .NET dependencies. This only calls MCP
initialize and tools/list; it never reads or changes Vault records.
"""

import argparse
import json
import pathlib
import queue
import subprocess
import sys
import threading


ROOT = pathlib.Path(__file__).resolve().parent.parent
DEFAULT_SERVER = ROOT / "bin" / "Release" / "net10.0-windows" / "WritingVaultMcp.exe"
DEFAULT_SETTINGS = ROOT / "appsettings.json"


def read_message(lines, timeout=30):
    try:
        line = lines.get(timeout=timeout)
    except queue.Empty as error:
        raise RuntimeError("The Release MCP server did not respond within 30 seconds") from error
    if not line:
        raise RuntimeError("The Release MCP server closed its stdio connection")
    return json.loads(line)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=pathlib.Path, default=DEFAULT_SERVER)
    parser.add_argument("--settings", type=pathlib.Path, default=DEFAULT_SETTINGS)
    options = parser.parse_args()

    server = options.server.resolve(strict=True)
    settings = json.loads(options.settings.read_text(encoding="utf-8-sig"))["WritingVault"]
    database = pathlib.Path(settings["DatabasePath"]).resolve(strict=True)
    backup_root = pathlib.Path(settings["BackupRoot"]).resolve(strict=True)
    manifest_path = server.parent / "manifest.json"
    server_args = [
        "serve", "--database", str(database), "--backup-root", str(backup_root),
        "--tool-surface", "v4", "--client-label", "Windows ODR",
    ]

    process = subprocess.Popen(
        [str(server), *server_args], cwd=server.parent,
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        text=True, encoding="utf-8", bufsize=1,
    )
    lines = queue.Queue()
    threading.Thread(target=lambda: [lines.put(line) for line in process.stdout], daemon=True).start()

    def send(message):
        process.stdin.write(json.dumps(message, separators=(",", ":")) + "\n")
        process.stdin.flush()

    def response_for(request_id):
        while True:
            message = read_message(lines)
            if message.get("id") == request_id:
                if "error" in message:
                    raise RuntimeError(f"MCP request {request_id} failed: {message['error']}")
                return message["result"]

    try:
        send({
            "jsonrpc": "2.0", "id": 1, "method": "initialize",
            "params": {
                "protocolVersion": "2025-06-18", "capabilities": {},
                "clientInfo": {"name": "writers-vault-windows-manifest", "version": "1.0.0"},
            },
        })
        initialize = response_for(1)
        send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        tools = []
        cursor = None
        request_id = 2
        while True:
            send({
                "jsonrpc": "2.0", "id": request_id, "method": "tools/list",
                "params": {"cursor": cursor} if cursor else {},
            })
            page = response_for(request_id)
            tools.extend(page["tools"])
            cursor = page.get("nextCursor")
            if not cursor:
                break
            request_id += 1
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)

    server_info = initialize["serverInfo"]
    manifest = {
        "manifest_version": "0.3",
        "name": server_info["name"],
        "version": server_info["version"],
        "description": "Writer's Vault canonical worldbuilding database for characters, relationships, objects, and timelines.",
        "author": {"name": "Writer's Vault"},
        "server": {
            "type": "binary",
            "entry_point": server.name,
            "mcp_config": {
                "command": "${__dirname}/" + server.name,
                "args": server_args,
                "env": {},
            },
        },
        "tools": [{"name": tool["name"], "description": tool.get("description", "")} for tool in tools],
        "tools_generated": False,
        "_meta": {
            "com.microsoft.windows": {
                "static_responses": {
                    "initialize": initialize,
                    "tools/list": {"tools": tools},
                },
            },
        },
    }
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "manifest": str(manifest_path), "serverName": server_info["name"],
        "serverVersion": server_info["version"], "toolCount": len(tools),
    }))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, RuntimeError) as error:
        print(f"Windows MCP manifest generation failed: {error}", file=sys.stderr)
        sys.exit(1)
