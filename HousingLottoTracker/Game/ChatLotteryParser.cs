using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HousingLottoTracker.Game;

// Parses the in-game chat confirmation emitted when you submit a lottery entry, e.g.
//   "You have submitted a lottery entry for plot 38, ward 30, Shirogane.
//    Your lottery number is 71. Results will be available from 3:00 a.m. 6/25/2026."
//
// This is the most reliable capture point: it fires exactly once at bid time and
// contains the plot, ward, district, your personal lottery number, and the precise
// results datetime — none of which the placard reliably exposes afterward.
public static class ChatLotteryParser
{
    public sealed class Parsed
    {
        public bool IsEntry;
        public int Plot;
        public int Ward;
        public string District = string.Empty;
        public int LotteryNumber = -1;
        public DateTime? ResultsLocal;   // parsed in the player's local time
    }

    // "submitted a lottery entry for plot 38, ward 30, Shirogane"
    private static readonly Regex EntryRx = new(
        @"lottery entry for plot\s+(?<plot>\d+),\s*ward\s+(?<ward>\d+),\s*(?<district>[A-Za-z'’\- ]+?)\.",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "Your lottery number is 71"
    private static readonly Regex NumberRx = new(
        @"lottery number is\s+(?<num>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "Results will be available from 3:00 a.m. 6/25/2026"
    private static readonly Regex ResultsRx = new(
        @"available from\s+(?<time>\d{1,2}:\d{2}\s*[ap]\.?m\.?)\s+(?<date>\d{1,2}/\d{1,2}/\d{4})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Parsed Parse(string message)
    {
        var p = new Parsed();
        if (string.IsNullOrWhiteSpace(message)) return p;

        // Normalise the unicode spaces / nbsp the game sometimes uses.
        var text = message.Replace('\u00A0', ' ').Trim();

        var em = EntryRx.Match(text);
        if (!em.Success) return p;  // not an entry confirmation

        p.IsEntry = true;
        p.Plot = ParseInt(em.Groups["plot"].Value);
        p.Ward = ParseInt(em.Groups["ward"].Value);
        p.District = em.Groups["district"].Value.Trim();

        var nm = NumberRx.Match(text);
        if (nm.Success) p.LotteryNumber = ParseInt(nm.Groups["num"].Value);

        var rm = ResultsRx.Match(text);
        if (rm.Success)
            p.ResultsLocal = ParseDateTime(rm.Groups["time"].Value, rm.Groups["date"].Value);

        return p;
    }

    private static int ParseInt(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;

    // Combine "3:00 a.m." + "6/25/2026" into a local DateTime. The game prints in the
    // client's locale; we try a few common layouts and fall back to null.
    private static DateTime? ParseDateTime(string timePart, string datePart)
    {
        // Normalise "a.m."/"p.m." to "AM"/"PM" for .NET parsing.
        var t = timePart.ToLowerInvariant()
            .Replace("a.m.", "AM").Replace("p.m.", "PM")
            .Replace("am", "AM").Replace("pm", "PM")
            .Replace(".", "").Trim();

        var combined = $"{datePart} {t}";

        string[] formats =
        {
            "M/d/yyyy h:mm tt",
            "M/d/yyyy hh:mm tt",
            "d/M/yyyy h:mm tt",
            "d/M/yyyy hh:mm tt",
            "yyyy/M/d h:mm tt",
        };

        foreach (var f in formats)
        {
            if (DateTime.TryParseExact(combined, f, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var dt))
                return dt;
        }

        // Last resort: loose parse.
        if (DateTime.TryParse(combined, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var loose))
            return loose;

        return null;
    }
}
