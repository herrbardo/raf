using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Raf.Client.Grpc;

/// <summary>Los tipos de <c>google/protobuf</c> que en JSON no se escriben como un objeto con sus campos.</summary>
internal static partial class WellKnown
{
    public const string Timestamp = "google.protobuf.Timestamp";
    public const string Duration = "google.protobuf.Duration";
    public const string FieldMask = "google.protobuf.FieldMask";
    public const string Struct = "google.protobuf.Struct";
    public const string Value = "google.protobuf.Value";
    public const string ListValue = "google.protobuf.ListValue";
    public const string Any = "google.protobuf.Any";

    private static readonly HashSet<string> Wrappers =
    [
        "google.protobuf.DoubleValue", "google.protobuf.FloatValue", "google.protobuf.Int64Value",
        "google.protobuf.UInt64Value", "google.protobuf.Int32Value", "google.protobuf.UInt32Value",
        "google.protobuf.BoolValue", "google.protobuf.StringValue", "google.protobuf.BytesValue",
    ];

    private static readonly HashSet<string> Special = [Timestamp, Duration, FieldMask, Struct, Value, ListValue, Any, .. Wrappers];

    public static bool IsSpecial(string fullName) => Special.Contains(fullName);

    public static bool IsWrapper(string fullName) => Wrappers.Contains(fullName);

    // Timestamp: RFC 3339 en UTC, con o sin fracción y con zona Z o un desfasaje.
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(\.\d{1,9})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex TimestampPattern();

    public static bool TryParseTimestamp(string text, out long seconds, out int nanos)
    {
        seconds = 0;
        nanos = 0;
        var match = TimestampPattern().Match(text.Trim());
        if (!match.Success) return false;
        var zone = match.Groups[3].Value.ToUpperInvariant() == "Z" ? "+00:00" : match.Groups[3].Value;
        if (!DateTimeOffset.TryParseExact(match.Groups[1].Value + zone, "yyyy-MM-dd'T'HH:mm:sszzz",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            return false;
        seconds = instant.ToUnixTimeSeconds();
        nanos = Fraction(match.Groups[2].Value);
        return true;
    }

    public static string FormatTimestamp(long seconds, int nanos)
    {
        var instant = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        return instant.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + FormatFraction(nanos) + "Z";
    }

    // Duration: segundos con hasta 9 decimales y la "s" al final ("1.5s", "-0.25s").
    [GeneratedRegex(@"^(-)?(\d+)(\.\d{1,9})?s$")]
    private static partial Regex DurationPattern();

    public static bool TryParseDuration(string text, out long seconds, out int nanos)
    {
        seconds = 0;
        nanos = 0;
        var match = DurationPattern().Match(text.Trim());
        if (!match.Success || !long.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out seconds)) return false;
        nanos = Fraction(match.Groups[3].Value);
        if (match.Groups[1].Success)
        {
            seconds = -seconds;
            nanos = -nanos;
        }
        return true;
    }

    public static string FormatDuration(long seconds, int nanos)
    {
        var negative = seconds < 0 || nanos < 0;
        var text = Math.Abs(seconds).ToString(CultureInfo.InvariantCulture) + FormatFraction(Math.Abs(nanos)) + "s";
        return negative ? "-" + text : text;
    }

    /// <summary>".5" → 500000000 nanosegundos.</summary>
    private static int Fraction(string text) =>
        text.Length <= 1 ? 0 : int.Parse(text[1..].PadRight(9, '0'), CultureInfo.InvariantCulture);

    /// <summary>Como la referencia de protobuf: sin fracción, o con 3, 6 o 9 decimales.</summary>
    private static string FormatFraction(int nanos) =>
        nanos == 0 ? ""
        : nanos % 1_000_000 == 0 ? "." + (nanos / 1_000_000).ToString("D3", CultureInfo.InvariantCulture)
        : nanos % 1_000 == 0 ? "." + (nanos / 1_000).ToString("D6", CultureInfo.InvariantCulture)
        : "." + nanos.ToString("D9", CultureInfo.InvariantCulture);

    /// <summary>"user.displayName" → "user.display_name": las rutas de un FieldMask viajan en snake_case.</summary>
    public static string ToSnakeCase(string path)
    {
        var text = new StringBuilder();
        foreach (var c in path)
        {
            if (char.IsAsciiLetterUpper(c)) text.Append('_').Append(char.ToLowerInvariant(c));
            else text.Append(c);
        }
        return text.ToString();
    }

    public static string ToCamelCase(string path)
    {
        var text = new StringBuilder();
        var upper = false;
        foreach (var c in path)
        {
            if (c == '_')
            {
                upper = true;
                continue;
            }
            text.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return text.ToString();
    }
}
