using System;
using System.Globalization;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HousingLottoTracker.Data;

namespace HousingLottoTracker.Game;

// What a single placard read produced. Any field may be "unknown" (the placard /
// game state didn't expose it this frame).
public sealed class PlacardSnapshot
{
    public bool Valid;                       // true if we got at least a plot location
    public ushort TerritoryTypeId;
    public string District = string.Empty;
    public byte Ward;                        // 1-based
    public byte Plot;                        // 1-based
    public LottoPlotSize Size = LottoPlotSize.Unknown;
    public bool IsFreeCompany;               // best-effort; default personal
    public LottoPhase Phase = LottoPhase.Unknown;
    public int EntryNumber = -1;             // your ticket, if the placard shows it
    public int WinningNumber = -1;           // shown during results
    public bool WonHint;                     // placard text suggests a win
    public DateTime? ResultsLocal;           // parsed from "Accepting entries until ..."
}

// Reads the housing lottery placard. The HousingSignBoard addon has no dedicated
// ClientStructs wrapper, so we read it as a generic AtkUnitBase and scrape its text
// nodes for entrant count / numbers / phrasing, while taking the precise plot
// location from HousingManager (which is populated while you stand at a placard).
public static unsafe class PlacardReader
{
    private const string AddonName = "HousingSignBoard";

    public static bool IsPlacardOpen(IGameGui gameGui)
        => gameGui.GetAddonByName(AddonName, 1) != nint.Zero;

    public static PlacardSnapshot Read(IGameGui gameGui, IDataManager data, ushort currentTerritoryType)
    {
        var snap = new PlacardSnapshot();

        // --- Plot location from HousingManager ---
        var housing = HousingManager.Instance();
        if (housing != null)
        {
            try
            {
                var ward = housing->GetCurrentWard();   // 0-based, -1 if not in a ward
                var plot = housing->GetCurrentPlot();    // 0-based, negatives = apartment sentinels
                var division = housing->GetCurrentDivision(); // 1 main, 2 subdivision

                if (ward >= 0)
                    snap.Ward = (byte)(ward + 1);

                if (plot >= 0)
                {
                    // Subdivision plots are numbered 31-60 in the UI; the API gives a
                    // 0-based plot within the division. Add the subdivision offset.
                    var plotNum = plot + 1;
                    if (division == 2) plotNum += 30;
                    snap.Plot = (byte)plotNum;
                }

                // GetOriginalHouseTerritoryTypeId is reliable inside a house but can
                // be 0 standing at an outdoor placard — fall back to the zone the
                // player is currently in (the housing ward's TerritoryType).
                snap.TerritoryTypeId = (ushort)HousingManager.GetOriginalHouseTerritoryTypeId();
                if (snap.TerritoryTypeId == 0)
                    snap.TerritoryTypeId = currentTerritoryType;

                snap.District = ResolveDistrict(data, snap.TerritoryTypeId);
                snap.Valid = snap.Ward > 0 && snap.Plot > 0;
            }
            catch { /* leave location unknown */ }
        }

        // --- Placard addon text scrape (address, phase, size) ---
        nint addr = gameGui.GetAddonByName(AddonName, 1);
        if (addr != nint.Zero)
        {
            var addon = (AtkUnitBase*)addr;
            if (addon != null && addon->IsVisible)
            {
                ScrapeSignboard(addon, snap);
            }
        }

        // --- Phase fallback from the global cycle clock ---
        if (snap.Phase == LottoPhase.Unknown)
            snap.Phase = LottoCycle.PhaseFor(DateTime.UtcNow);

        return snap;
    }

    // Scrape the signboard's visible text nodes. We can't rely on fixed node indices
    // across patches, so we classify each text node by keyword / numeric content.
    private static void ScrapeSignboard(AtkUnitBase* addon, PlacardSnapshot snap)
    {
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Text) continue;

            var raw = ((AtkTextNode*)node)->NodeText.ToString();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var text = raw.Trim();
            var lower = text.ToLowerInvariant();

            // Phase hints. The placard reads "Selling via lottery. (Current
            // participants: N)" during entry; results wording differs.
            if (lower.Contains("selling via lottery") || lower.Contains("accepting entries")
                || lower.Contains("current participants"))
                snap.Phase = LottoPhase.Entry;
            if (lower.Contains("winning number") || lower.Contains("results are in")
                || lower.Contains("lottery results"))
                snap.Phase = LottoPhase.Results;

            // Size hints (placard "Plot Size" field shows Small/Medium/Large).
            if (snap.Size == LottoPlotSize.Unknown)
            {
                if (lower.Contains("small")) snap.Size = LottoPlotSize.Small;
                else if (lower.Contains("medium")) snap.Size = LottoPlotSize.Medium;
                else if (lower.Contains("large")) snap.Size = LottoPlotSize.Large;
            }

            // FC vs personal. Placard: "Available to free companies. Available to
            // private buyers." If only FCs are eligible, treat as an FC plot.
            if (lower.Contains("unavailable to private") || lower.Contains("free companies only"))
                snap.IsFreeCompany = true;

            // Address line: "Plot 38, 30th Ward, Shirogane".
            if (lower.Contains("ward") && lower.Contains("plot"))
                ParseAddress(text, snap);

            // Your entry number, if the placard ever shows it.
            if ((lower.Contains("your entry") || lower.Contains("your lottery number")) && snap.EntryNumber < 0)
            {
                var n = ExtractFirstInt(text);
                if (n >= 0) snap.EntryNumber = n;
            }

            // Winning number during results, e.g. "Winning Number: 5".
            if (lower.Contains("winning number") && snap.WinningNumber < 0)
            {
                var n = ExtractFirstInt(text);
                if (n >= 0) snap.WinningNumber = n;
            }

            // Win phrasing.
            if (lower.Contains("congratulations") || lower.Contains("you have won")
                || lower.Contains("won the lottery"))
                snap.WonHint = true;

            // Entry deadline: "Accepting entries until 2:59 a.m. 6/25/2026." The
            // deadline is one minute before results open, so this gives us the real
            // results datetime (rounded up to the top of the hour).
            if (snap.ResultsLocal == null &&
                (lower.Contains("accepting entries until") || lower.Contains("entries until")))
            {
                var dt = ChatLotteryParser.ParseDeadline(text);
                if (dt != null)
                {
                    // 2:59 -> 3:00; round up to the next minute boundary's hour mark.
                    var d = dt.Value;
                    if (d.Minute == 59) d = d.AddMinutes(1);
                    snap.ResultsLocal = d;
                }
            }
        }
    }

    // Parse the placard address line "Plot 38, 30th Ward, Shirogane".
    private static void ParseAddress(string text, PlacardSnapshot snap)
    {
        try
        {
            var plotM = System.Text.RegularExpressions.Regex.Match(text, @"plot\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var wardM = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\w*\s+ward", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (plotM.Success && int.TryParse(plotM.Groups[1].Value, out var plot) && plot is > 0 and < 256)
                snap.Plot = (byte)plot;
            if (wardM.Success && int.TryParse(wardM.Groups[1].Value, out var ward) && ward is > 0 and < 256)
                snap.Ward = (byte)ward;

            // District is the trailing comma-separated segment.
            var parts = text.Split(',');
            if (parts.Length >= 3)
            {
                var d = parts[^1].Trim().TrimEnd('.');
                var id = DistrictNameToTerritoryId(d);
                if (id != 0) snap.TerritoryTypeId = id;
            }
        }
        catch { /* ignore */ }
    }

    // Pull the first run of digits (allowing thousands separators) from a string.
    private static int ExtractFirstInt(string s)
    {
        int i = 0;
        while (i < s.Length && !char.IsDigit(s[i])) i++;
        if (i >= s.Length) return -1;
        int j = i;
        var sb = new System.Text.StringBuilder();
        while (j < s.Length && (char.IsDigit(s[j]) || s[j] == ',' ))
        {
            if (char.IsDigit(s[j])) sb.Append(s[j]);
            j++;
        }
        if (sb.Length == 0) return -1;
        return int.TryParse(sb.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;
    }

    public static string ResolveDistrict(IDataManager data, ushort territoryTypeId)
    {
        if (territoryTypeId == 0) return string.Empty;
        try
        {
            var tt = data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territoryTypeId);
            if (tt.HasValue)
            {
                var place = tt.Value.PlaceName.Value.Name.ExtractText();
                if (!string.IsNullOrEmpty(place)) return place;
            }
        }
        catch { /* ignore */ }
        return $"Territory {territoryTypeId}";
    }

    // The five residential districts and their well-known TerritoryType ids. Used by
    // the manual-add form so a backfilled bid lands on the same key a live capture
    // would produce. Display names are resolved from the sheet at runtime where
    // possible, but these ids are stable across patches.
    public static readonly (string Name, ushort TerritoryTypeId)[] Districts =
    {
        ("Mist", 339),
        ("The Lavender Beds", 340),
        ("The Goblet", 341),
        ("Shirogane", 641),
        ("Empyreum", 979),
    };

    // Map a district name (as it appears in chat / on the placard, e.g. "Shirogane"
    // or "The Lavender Beds") to its TerritoryType id. Tolerant of a missing/extra
    // leading "The" and case. Returns 0 if unrecognised.
    public static ushort DistrictNameToTerritoryId(string district)
    {
        if (string.IsNullOrWhiteSpace(district)) return 0;
        var norm = district.Trim().TrimStart();
        foreach (var (name, id) in Districts)
        {
            if (name.Equals(norm, StringComparison.OrdinalIgnoreCase)) return id;
            // Compare with leading "The " stripped from both sides.
            var a = name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
            var b = norm.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? norm[4..] : norm;
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return id;
        }
        return 0;
    }

    // Region resolution copied from FC Tracker's proven World -> DC -> Region path.
    public static string ResolveRegionCode(IDataManager data, string worldName)
    {
        if (string.IsNullOrEmpty(worldName)) return "";
        try
        {
            var worlds = data.GetExcelSheet<Lumina.Excel.Sheets.World>();
            if (worlds == null) return "";

            foreach (var w in worlds)
            {
                if (!w.Name.ExtractText().Equals(worldName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var dcRef = w.DataCenter;
                var regionId = (byte)dcRef.Value.Region.RowId;

                var regionName = string.Empty;
                try
                {
                    var dcGroup = data.GetExcelSheet<Lumina.Excel.Sheets.WorldDCGroupType>()
                        ?.GetRowOrDefault((uint)regionId);
                    if (dcGroup.HasValue) regionName = dcGroup.Value.Name.ExtractText();
                }
                catch { /* ignore */ }

                return RegionAlias(regionName, regionId);
            }
        }
        catch { /* ignore */ }
        return "";
    }

    private static string RegionAlias(string regionName, byte regionId)
    {
        switch (regionName)
        {
            case "": break;
            case "Japan": return "JP";
            case "North America": return "NA";
            case "Europe": return "EU";
            case "Oceania": return "OCE";
            case "China": return "CN";
            case "Korea": return "KR";
            case "NA Cloud": return "CLD";
            case "Traditional Chinese regions": return "TCN";
        }

        return regionId switch
        {
            1 => "JP",
            2 => "NA",
            3 => "EU",
            4 => "OCE",
            5 => "CN",
            6 => "KR",
            7 => "CLD",
            _ => string.IsNullOrEmpty(regionName) ? "DEV" : regionName,
        };
    }

    // Read the prompt text from a SelectYesno addon (used for win auto-detect).
    public static string ReadSelectYesnoPrompt(nint addonPtr)
    {
        if (addonPtr == nint.Zero) return string.Empty;
        var addon = (AtkUnitBase*)addonPtr;
        if (addon == null) return string.Empty;

        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Text) continue;
            var t = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(t) && t.Length > 10)
                return t;
        }
        return string.Empty;
    }

    // Concatenate all visible text nodes of an addon into one blob (newline-joined).
    public static string ReadAllText(AtkUnitBase* addon)
    {
        if (addon == null) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Text) continue;
            var t = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(t)) sb.AppendLine(t.Trim());
        }
        return sb.ToString();
    }

    // Scan all loaded, visible addons for the "Housing Lottery Status" detail popup
    // (the one opened from Duty > Timers > Estate). We don't rely on a fixed addon
    // name because it isn't documented in ClientStructs; instead we match on the
    // text signature. Returns the popup's concatenated text, or "" if not open.
    public static string FindLotteryStatusText()
    {
        try
        {
            var mgr = RaptureAtkUnitManager.Instance();
            if (mgr == null) return string.Empty;

            ref var list = ref mgr->AtkUnitManager.AllLoadedUnitsList;
            var count = list.Count;
            for (var i = 0; i < count && i < 256; i++)
            {
                var addon = list.Entries[i].Value;
                if (addon == null || !addon->IsVisible) continue;

                var text = ReadAllText(addon);
                if (string.IsNullOrEmpty(text)) continue;

                var lower = text.ToLowerInvariant();
                // Signature unique to the housing lottery status detail panel.
                if (lower.Contains("lottery number") &&
                    (lower.Contains("type of entry") || lower.Contains("current entry")))
                {
                    return text;
                }
            }
        }
        catch { /* ignore */ }
        return string.Empty;
    }
}

