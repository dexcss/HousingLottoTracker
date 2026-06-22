using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace HousingLottoTracker.Data;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Keyed by BidRecord.Key. List keeps Newtonsoft serialization simple.
    public List<BidRecord> Bids = new();

    public bool OpenOnLogin = false;

    // Shared, machine-wide storage (ignores --roamingPath). On by default to match
    // FC Tracker behaviour so all clients share one bid list.
    public bool UseSharedStorage = true;
    public string SharedStoragePathOverride = string.Empty;

    // View options.
    public bool HidePastCycles = false;     // hide bids whose claim window has closed
    public bool ShowAccountColumn = false;

    // Per-column visibility. Defaults give a sensible at-a-glance layout.
    public bool ColCharacter = true;
    public bool ColRegion = false;
    public bool ColDistrict = true;
    public bool ColWardPlot = true;
    public bool ColSize = true;
    public bool ColType = true;          // FC / Personal
    public bool ColEntryNumber = true;
    public bool ColEntryDate = true;
    public bool ColResultsDate = true;
    public bool ColCountdown = true;     // live countdown to results / claim deadline
    public bool ColPhase = false;
    public bool ColOutcome = true;
    public bool ColNotes = false;

    // Column display order (by name). Missing columns are appended in default order.
    public List<string> ColumnOrder = new();

    // true = draggable column borders; false = auto-fit (matches FC Tracker option).
    public bool ManualColumnResize = false;

    // Account aliases: roaming path -> friendly name.
    public Dictionary<string, string> AccountAliases = new();

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;

    public void Save() => pluginInterface?.SavePluginConfig(this);
}
