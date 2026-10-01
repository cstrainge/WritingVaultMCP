using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WritingVaultMcp.Infrastructure.Access;

internal static class VaultJournalPayload
{
    private static readonly HashSet<string> OmittedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "operationId", "clientLabel"
    };

    private static readonly HashSet<string> SensitiveLongFormFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "body", "content", "description", "notes", "citation", "claimText", "commentary",
        "evidenceExcerpt", "summary", "impact", "outcome", "physicalDescription", "personalitySummary"
    };

    public static string Serialize(object? value, JsonSerializerOptions options)
    {
        var node = JsonSerializer.SerializeToNode(value, options);
        Sanitize(node);
        return node?.ToJsonString(options) ?? "null";
    }

    private static void Sanitize(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (OmittedFields.Contains(property.Key))
                {
                    obj.Remove(property.Key);
                    continue;
                }

                if (SensitiveLongFormFields.Contains(property.Key))
                {
                    obj[property.Key] = Redact(property.Value);
                    continue;
                }

                Sanitize(property.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array) Sanitize(item);
        }
    }

    private static JsonNode? Redact(JsonNode? value)
    {
        if (value is null) return null;
        if (value is JsonObject patch && patch.TryGetPropertyValue("specified", out var specified))
        {
            var result = new JsonObject { ["specified"] = specified?.DeepClone() };
            if (patch.TryGetPropertyValue("value", out var patchValue)) result["value"] = RedactValue(patchValue);
            return result;
        }
        return RedactValue(value);
    }

    private static JsonNode? RedactValue(JsonNode? value)
    {
        if (value is null) return null;
        string? text;
        try { text = value.GetValue<string>(); }
        catch (InvalidOperationException) { return JsonValue.Create("[redacted]"); }
        return new JsonObject
        {
            ["redacted"] = true,
            ["length"] = text.Length,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
        };
    }
}
