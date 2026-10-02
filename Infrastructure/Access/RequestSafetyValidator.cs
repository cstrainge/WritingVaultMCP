using System.Collections;
using System.Reflection;
using WritingVaultMcp.Application;

namespace WritingVaultMcp.Infrastructure.Access;

/// <summary>Uniform limits applied to every mutation before serialization or OleDb access.</summary>
internal static class RequestSafetyValidator
{
    private const int LongTextLimit = 1_000_000;
    private static readonly IReadOnlyDictionary<string, int> StringLimits = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["ClientLabel"] = 100, ["Name"] = 255, ["Title"] = 255, ["Alias"] = 255,
        ["Label"] = 255, ["Role"] = 100, ["NewRole"] = 255, ["LocationRole"] = 100, ["SourceType"] = 100,
        ["AuthorPublisher"] = 255, ["CanonicalUrl"] = 2048, ["ArchiveUrl"] = 2048,
        ["MediaType"] = 100, ["EvidenceRelation"] = 30, ["ClaimStatus"] = 30,
        ["TargetField"] = 100, ["Locator"] = 255, ["ReferenceTimeZoneId"] = 100,
        ["DefaultTimeZoneId"] = 100, ["TimeZoneId"] = 100,
        ["BirthLocationDetail"] = 255, ["MiddleNames"] = 255, ["FamilyName"] = 100,
        ["PreferredName"] = 100, ["Gender"] = 100, ["Pronouns"] = 100, ["Species"] = 512, ["Race"] = 100,
        ["Occupation"] = 255, ["Nationality"] = 100,
        ["CalendarId"] = 50, ["Citation"] = LongTextLimit, ["Description"] = LongTextLimit,
        ["Notes"] = LongTextLimit, ["Body"] = LongTextLimit, ["Content"] = LongTextLimit,
        ["ClaimText"] = LongTextLimit, ["Commentary"] = LongTextLimit,
        ["EvidenceExcerpt"] = LongTextLimit, ["Summary"] = LongTextLimit,
        ["Impact"] = LongTextLimit, ["Outcome"] = LongTextLimit,
        ["PhysicalDescription"] = LongTextLimit, ["PersonalitySummary"] = LongTextLimit
    };

    public static string? Validate(object value)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return ValidateValue(value, value.GetType().Name, visited, 0);
    }

    private static string? ValidateValue(object? value, string path, HashSet<object> visited, int depth)
    {
        if (value is null) return null;
        if (depth > 8) return $"{path} exceeds the supported input nesting depth.";
        var type = value.GetType();
        if (type.IsEnum)
            return Enum.IsDefined(type, value) ? null : $"{path} is not a supported {type.Name} value.";
        if (value is double doubleValue && !double.IsFinite(doubleValue)) return $"{path} must be finite.";
        if (value is float floatValue && !float.IsFinite(floatValue)) return $"{path} must be finite.";
        if (type.IsPrimitive || value is decimal or DateTime or DateTimeOffset or DateOnly or Guid) return null;
        if (value is string text) return text.Length > LongTextLimit ? $"{path} exceeds {LongTextLimit} characters." : null;
        if (!type.IsValueType && !visited.Add(value)) return null;

        if (value is IEnumerable items)
        {
            var count = 0;
            foreach (var item in items)
            {
                if (++count > 100) return $"{path} supports at most 100 items.";
                if (ValidateValue(item, $"{path}[{count - 1}]", visited, depth + 1) is { } itemError) return itemError;
            }
            return null;
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0) continue;
            var propertyValue = property.GetValue(value);
            var propertyPath = $"{path}.{property.Name}";
            if (propertyValue is string field && StringLimits.TryGetValue(property.Name, out var limit) && field.Length > limit)
                return $"{propertyPath} exceeds {limit} characters.";
            if (propertyValue is PatchField<string> patch && patch.Specified && patch.Value is { } patchText &&
                StringLimits.TryGetValue(property.Name, out var patchLimit) && patchText.Length > patchLimit)
                return $"{propertyPath} exceeds {patchLimit} characters.";
            if (propertyValue is int number &&
                (property.Name.EndsWith("Id", StringComparison.Ordinal) || property.Name == "ExpectedVersion") && number <= 0)
            {
                // Private memories use zero as an explicit create-if-absent version.
                if (!(value is Mcp.V4.V4MemorySaveRequest && property.Name == "ExpectedVersion" && number == 0))
                    return $"{propertyPath} must be positive.";
            }
            if (property.Name == "EntityIds" && propertyValue is IEnumerable entityIds)
            {
                foreach (var id in entityIds)
                    if (id is int entityId && entityId <= 0) return $"{propertyPath} values must be positive.";
            }
            if (ValidateValue(propertyValue, propertyPath, visited, depth + 1) is { } error) return error;
        }
        return null;
    }
}
