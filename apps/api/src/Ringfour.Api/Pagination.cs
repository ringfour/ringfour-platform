using System.Text;
using Ringfour.Api.Errors;

namespace Ringfour.Api;

// Opaque cursor pagination shared by all list endpoints. A cursor is the
// base64url of "createdAt round-trip|tie-break id" of the last item returned;
// round-trip formatting preserves the exact value read from PostgreSQL so
// equality comparisons hold.
internal static class Pagination
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 200;

    public static int ClampLimit(int? limit)
    {
        if (limit is null)
        {
            return DefaultPageSize;
        }
        if (limit < 1 || limit > MaxPageSize)
        {
            throw ApiException.InvalidInput($"limit must be between 1 and {MaxPageSize}.");
        }
        return limit.Value;
    }

    public static (DateTimeOffset? CreatedAt, Guid? Id) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (null, null);
        }
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(cursor)));
            var parts = decoded.Split('|');
            return (
                DateTimeOffset.ParseExact(
                    parts[0], "o", System.Globalization.CultureInfo.InvariantCulture),
                Guid.Parse(parts[1]));
        }
        catch (Exception exception) when (exception is FormatException or IndexOutOfRangeException)
        {
            throw ApiException.InvalidInput("cursor is not valid.");
        }
    }

    public static string EncodeCursor(DateTimeOffset createdAt, Guid id)
    {
        var raw = Encoding.UTF8.GetBytes($"{createdAt:o}|{id}");
        return Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string? NextCursor<T>(
        bool hasMore,
        IReadOnlyList<T> page,
        Func<T, DateTimeOffset> createdAt,
        Func<T, Guid> tieBreakId)
    {
        if (!hasMore || page.Count == 0)
        {
            return null;
        }
        var last = page[^1];
        return EncodeCursor(createdAt(last), tieBreakId(last));
    }

    private static string PadBase64(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
    }
}
