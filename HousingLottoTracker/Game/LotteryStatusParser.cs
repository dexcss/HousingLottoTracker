using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HousingLottoTracker.Game;

// Parses the "Housing Lottery Status" detail popup opened from the Timers window
// (Duty > Timers > Estate > Housing Lottery Status). Unlike the chat confirmation,
// this panel is available any time during the entry period, so it lets a user
// backfill a bid they placed before installing the plugin. It also reports the
// entry type (Private Buyer vs Free Company), which the chat line does not.
//
// Example text content:
//   Current Entry
//   Plot 38, 30th Ward, Shirogane (Medium)
//   Lottery Number: 71
//   Type of Entry: Private Buyer
public static class LotteryStatusParser
{
    public sealed class Parsed
    {
        public bool HasEntry;
        public int Plot = -1;
        public int Ward = -1;
        public string District = string.Empty;
        public LottoSizeHint Size = LottoSizeHint.Unknown;
        public int LotteryNumber = -1;
        public bool IsFreeCompany;
    }

    public enum LottoSizeHint { Unknown, Small, Medium, Large }

    // "Plot 38, 30th Ward, Shirogane (Medium)" — size in parens optional.
    private static readonly Regex AddressRx = new(
        @"plot\s+(?<plot>\d+),\s*(?<ward>\d+)\w*\s+ward,\s*(?<district>[A-Za-z'’\- ]+?)(?:\s*\((?<size>small|medium|large)\))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex NumberRx = new(
        @"lottery number:?\s*(?<num>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TypeRx = new(
        @"type of entry:?\s*(?<type>.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Parse from the concatenated visible text of the popup. Lines may arrive joined
    // by spaces or newlines depending on how the addon nodes are read, so we run the
    // line-oriented regexes against the whole blob.
    public static Parsed Parse(string text)
    {
        var p = new Parsed();
        if (string.IsNullOrWhiteSpace(text)) return p;

        var norm = text
            .Replace('\u00A0', ' ')
            .Replace('\u202F', ' ')
            .Replace('\u2007', ' ');

        // The panel only describes an active entry when it mentions "Current Entry"
        // (entry period) — otherwise there's nothing to record.
        if (norm.IndexOf("current entry", StringComparison.OrdinalIgnoreCase) < 0
            && norm.IndexOf("lottery number", StringComparison.OrdinalIgnoreCase) < 0)
            return p;

        var addr = AddressRx.Match(norm);
        if (addr.Success)
        {
            p.Plot = ParseInt(addr.Groups["plot"].Value);
            p.Ward = ParseInt(addr.Groups["ward"].Value);
            p.District = addr.Groups["district"].Value.Trim();
            var sz = addr.Groups["size"].Value.ToLowerInvariant();
            p.Size = sz switch
            {
                "small" => LottoSizeHint.Small,
                "medium" => LottoSizeHint.Medium,
                "large" => LottoSizeHint.Large,
                _ => LottoSizeHint.Unknown,
            };
        }

        var num = NumberRx.Match(norm);
        if (num.Success) p.LotteryNumber = ParseInt(num.Groups["num"].Value);

        var type = TypeRx.Match(norm);
        if (type.Success)
        {
            var t = type.Groups["type"].Value.ToLowerInvariant();
            // "Free Company" => FC; "Private Buyer" / "Individual" => personal.
            p.IsFreeCompany = t.Contains("free company") || t.Contains("free comp");
        }

        // Consider it a usable entry only if we at least got a plot+ward and a number.
        p.HasEntry = p.Plot > 0 && p.Ward > 0 && p.LotteryNumber >= 0;
        return p;
    }

    private static int ParseInt(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;
}
