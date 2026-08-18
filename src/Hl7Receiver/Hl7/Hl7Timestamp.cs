using System.Globalization;
using System.Text.RegularExpressions;

namespace Hl7Receiver.Hl7;

/// <summary>
/// Converts HL7 TS/DTM values (<c>YYYY[MM[DD[HH[MM[SS[.S[S[S[S]]]]]]]]][+/-ZZZZ]</c>) to ISO-8601 for storage,
/// preserving the precision that was sent (a date-only DOB stays a date). No timezone is invented: if the sender
/// omitted the offset (Woodbine's samples do), the result has none — see README for the Eastern-time assumption.
/// </summary>
public static partial class Hl7Timestamp
{
    [GeneratedRegex(@"^(?<y>\d{4})(?<mo>\d{2})?(?<d>\d{2})?(?<h>\d{2})?(?<mi>\d{2})?(?<s>\d{2})?(?<f>\.\d{1,4})?(?<tz>[+-]\d{4})?$")]
    private static partial Regex Pattern();

    /// <summary>Returns ISO-8601 text, or null if the value is absent or not a valid HL7 timestamp.</summary>
    public static string? ToIso8601(string? hl7)
    {
        if (string.IsNullOrWhiteSpace(hl7))
        {
            return null;
        }

        var m = Pattern().Match(hl7.Trim());
        if (!m.Success)
        {
            return null;
        }

        var year = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
        var result = year.ToString("D4", CultureInfo.InvariantCulture);

        if (!TryAppend(m, "mo", 1, 12, "-", ref result)) return null;
        if (!TryAppend(m, "d", 1, 31, "-", ref result)) return null;
        if (!TryAppend(m, "h", 0, 23, "T", ref result)) return null;
        if (m.Groups["h"].Success && !m.Groups["mi"].Success)
        {
            result += ":00"; // an hour without minutes is legal HL7 but not legal ISO
        }
        if (!TryAppend(m, "mi", 0, 59, ":", ref result)) return null;
        if (!TryAppend(m, "s", 0, 59, ":", ref result)) return null;

        if (m.Groups["f"].Success)
        {
            result += m.Groups["f"].Value; // ".ffff"
        }

        if (m.Groups["tz"].Success)
        {
            var tz = m.Groups["tz"].Value; // +HHMM
            result += $"{tz[..3]}:{tz[3..]}";
        }

        return result;
    }

    private static bool TryAppend(Match m, string group, int min, int max, string separator, ref string result)
    {
        if (!m.Groups[group].Success)
        {
            return true;
        }

        var value = int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
        if (value < min || value > max)
        {
            return false;
        }

        result += separator + m.Groups[group].Value;
        return true;
    }
}
