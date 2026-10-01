using System.Data.Common;

namespace WritingVaultMcp.Infrastructure.Access;

internal static class DbRecordExtensions
{
    public static int Int32(this DbDataReader reader, string column) =>
        reader.GetInt32(reader.GetOrdinal(column));

    public static string String(this DbDataReader reader, string column) =>
        reader.GetString(reader.GetOrdinal(column));

    public static string? NullableString(this DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static int? NullableInt32(this DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    public static DateTime DateTime(this DbDataReader reader, string column) =>
        reader.GetDateTime(reader.GetOrdinal(column));

    public static DateTime? NullableDateTime(this DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }
}
