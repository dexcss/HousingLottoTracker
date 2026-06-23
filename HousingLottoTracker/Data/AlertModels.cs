using System;
using System.Collections.Generic;
using System.Linq;

namespace HousingLottoTracker.Data;

// An open lottery plot as reported by PaissaDB (crowd-sourced). Only the fields we
// need for matching and display.
public class OpenPlot
{
    public ushort WorldId;
    public string WorldName = string.Empty;
    public string DataCenter = string.Empty;
    public string Region = string.Empty;
    public ushort DistrictId;
    public string District = string.Empty;
    public int Ward;          // 1-based for display
    public int Plot;          // 1-based for display
    public LottoPlotSize Size = LottoPlotSize.Unknown;
    public bool IsLottery;
    public DateTime SeenUtc = DateTime.UtcNow;

    // Stable identity for an open-plot instance within a lottery cycle. Used to
    // decide whether an alert is "new" vs already acknowledged.
    public string Key => $"{WorldId}:{DistrictId}:{Ward}:{Plot}";

    public string SizeText => Size switch
    {
        LottoPlotSize.Small => "Small",
        LottoPlotSize.Medium => "Medium",
        LottoPlotSize.Large => "Large",
        _ => "?",
    };
}

// A user-defined watch. Empty filter lists mean "any". A plot opening that matches
// ALL set filters fires an alert.
[Serializable]
public class AlertRule
{
    public string Id = Guid.NewGuid().ToString("N");
    public bool Enabled = true;
    public string Label = string.Empty;          // optional friendly name

    // District is required (one of the five); 0 = any district.
    public ushort DistrictId;                    // 339/340/341/641/979, or 0 = any

    // Optional filters. Empty = any.
    public List<int> Plots = new();              // e.g. [60]; empty = any plot
    public List<int> Wards = new();              // e.g. [26]; empty = any ward
    public List<string> Worlds = new();          // world names; empty = any world
    public List<string> DataCenters = new();     // DC names; empty = any DC
    public List<string> Regions = new();         // region codes; empty = any region
    public List<LottoPlotSize> Sizes = new();    // empty = any size

    public bool Matches(OpenPlot p)
    {
        if (!Enabled) return false;
        if (!p.IsLottery) return false;
        if (DistrictId != 0 && p.DistrictId != DistrictId) return false;
        if (Plots.Count > 0 && !Plots.Contains(p.Plot)) return false;
        if (Wards.Count > 0 && !Wards.Contains(p.Ward)) return false;
        if (Worlds.Count > 0 && !Worlds.Contains(p.WorldName, StringComparer.OrdinalIgnoreCase)) return false;
        if (DataCenters.Count > 0 && !DataCenters.Contains(p.DataCenter, StringComparer.OrdinalIgnoreCase)) return false;
        if (Regions.Count > 0 && !Regions.Contains(p.Region, StringComparer.OrdinalIgnoreCase)) return false;
        if (Sizes.Count > 0 && !Sizes.Contains(p.Size)) return false;
        return true;
    }

    public string Describe()
    {
        if (!string.IsNullOrWhiteSpace(Label)) return Label;
        var district = DistrictId == 0 ? "Any district" : Game.PlacardReader.DistrictDisplayName(DistrictId);
        var plot = Plots.Count > 0 ? "Plot " + string.Join("/", Plots) : "any plot";
        var loc = Worlds.Count > 0 ? string.Join("/", Worlds)
                : DataCenters.Count > 0 ? string.Join("/", DataCenters)
                : Regions.Count > 0 ? string.Join("/", Regions)
                : "any server";
        var ward = Wards.Count > 0 ? ", ward " + string.Join("/", Wards) : "";
        var size = Sizes.Count > 0 ? " (" + string.Join("/", Sizes) + ")" : "";
        return $"{district} {plot}{ward} on {loc}{size}";
    }
}

// A live alert: a matched open plot awaiting acknowledgement.
public class ActiveAlert
{
    public string RuleId = string.Empty;
    public string RuleLabel = string.Empty;
    public OpenPlot Plot = new();
    public DateTime FirstSeenUtc = DateTime.UtcNow;

    // Acknowledgement key = rule + plot instance, so re-opening the SAME plot after
    // it's filled doesn't re-alert until it genuinely becomes available again.
    public string AckKey => $"{RuleId}:{Plot.Key}";
}
