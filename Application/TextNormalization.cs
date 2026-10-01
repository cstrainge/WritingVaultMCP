using System.Globalization;
using System.Text;

namespace WritingVaultMcp.Application;

public static class TextNormalization
{
    public static string CanonicalKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        var collapsed = string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.ToUpper(CultureInfo.InvariantCulture);
    }

    public static string Required(string value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{field} is required.", field);
        var trimmed = value.Trim();
        if (trimmed.Length > maximumLength) throw new ArgumentOutOfRangeException(field, $"{field} cannot exceed {maximumLength} characters.");
        return trimmed;
    }
}
