using System.Text.Json;

namespace WritingVaultMcp.Infrastructure;

internal static class VaultDiagnostics
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static void Write(
        string eventName,
        string level = "information",
        string? operationId = null,
        string? commandType = null,
        string? code = null,
        int? attempt = null,
        int? pendingWrites = null,
        double? durationMilliseconds = null)
    {
        var entry = new Dictionary<string, object?>
        {
            ["timestampUtc"] = DateTime.UtcNow,
            ["level"] = level,
            ["event"] = eventName
        };
        if (operationId is not null) entry["operationId"] = operationId;
        if (commandType is not null) entry["commandType"] = commandType;
        if (code is not null) entry["code"] = code;
        if (attempt is not null) entry["attempt"] = attempt;
        if (pendingWrites is not null) entry["pendingWrites"] = pendingWrites;
        if (durationMilliseconds is not null) entry["durationMs"] = Math.Round(durationMilliseconds.Value, 1);
        lock (Sync) Console.Error.WriteLine(JsonSerializer.Serialize(entry, Options));
    }
}
