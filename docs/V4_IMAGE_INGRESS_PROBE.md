# v4 Phase 1 image-ingress probe

The probe proves that each supported chat client can transmit real PNG bytes to the local MCP boundary before image storage is implemented. It exists only in Debug builds, opens no database, accepts no file path or remote URL, hashes and validates the PNG header in memory, zeroes the decoded buffer, and records metadata only when an evidence path is configured.

Build Debug, then configure a temporary local MCP server with:

```text
command: dotnet
arguments:
  .\bin\Debug\net10.0-windows\WritingVaultMcp.dll
  contract
  image-probe
  --evidence-output
  .\artifacts\contracts\v4\image-ingress-evidence.jsonl
```

The only tool is `image_ingress_probe`. The client must attach a real PNG and submit its bytes in exactly one of these forms:

```json
{
  "clientName": "ChatGPT",
  "mediaType": "image/png",
  "dataBase64": "<base64 from the attached PNG>"
}
```

or:

```json
{
  "clientName": "Claude",
  "mediaType": "image/png",
  "dataUrl": "data:image/png;base64,<base64 from the attached PNG>"
}
```

Success returns transport, media type, decoded byte count, dimensions, and SHA-256. The evidence JSONL stores those fields plus real UTC receipt time; it never stores encoded or decoded content. The Phase 1 review records which representation each client actually produced, its successful payload size, and its client-visible over-limit behavior. Automated fixture calls use `clientName: "AutomatedTest"` and do not count as either host proof.

This Debug probe remains available for diagnosis. The user moved the two real-client PNG checks to Phase 12 validation against the v4 Release deployment on 2026-09-30; they do not block switch-over. A copied base64 fixture or a server-local attachment path does not count as real-client evidence.

For a separate Debug diagnosis, stop the Debug Writing Vault tunnel and run [`Run-WritingVault-Image-Probe-Tunnel.bat`](../Run-WritingVault-Image-Probe-Tunnel.bat). It uses the Debug tunnel ID and Debug DPAPI-protected key, creates a temporary profile, and serves only the Debug probe. Stop it after the test and restart the normal Debug tunnel launcher. The Debug mutex prevents those two Debug profiles from running together; the Release tunnel stays independent. The required Phase 12 client PNG check still runs against the Release deployment.

Claude Desktop can use the direct stdio configuration above. Remove the temporary server after the probe and reconnect the normal Writing Vault entry.
