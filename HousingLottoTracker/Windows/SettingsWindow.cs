using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using HousingLottoTracker.Data;

namespace HousingLottoTracker.Windows;

public class SettingsWindow : Window
{
    private readonly Plugin plugin;
    private bool confirmClear;

    public SettingsWindow(Plugin plugin)
        : base("Housing Lotto Tracker — Settings###HousingLottoSettings")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 360) * ImGuiHelpers.GlobalScale,
            MaximumSize = new Vector2(900, 1200),
        };
    }

    // Column toggle metadata: (display label, getter, setter, order-name).
    private readonly (string Label, string Name)[] columns =
    {
        ("Login button (LOG)", "Login"),
        ("Travel button (TP)", "Tp"),
        ("Character", "Character"),
        ("Region", "Region"),
        ("District", "District"),
        ("Ward/Plot", "WardPlot"),
        ("Size", "Size"),
        ("Type (FC/Personal)", "Type"),
        ("Your number", "EntryNumber"),
        ("Entered date", "EntryDate"),
        ("Results date", "ResultsDate"),
        ("Countdown", "Countdown"),
        ("Phase", "Phase"),
        ("Outcome", "Outcome"),
        ("Notes", "Notes"),
    };

    private static bool Get(Configuration c, string name) => name switch
    {
        "Login" => c.ColLogin,
        "Tp" => c.ColTp,
        "Character" => c.ColCharacter,
        "Region" => c.ColRegion,
        "District" => c.ColDistrict,
        "WardPlot" => c.ColWardPlot,
        "Size" => c.ColSize,
        "Type" => c.ColType,
        "EntryNumber" => c.ColEntryNumber,
        "EntryDate" => c.ColEntryDate,
        "ResultsDate" => c.ColResultsDate,
        "Countdown" => c.ColCountdown,
        "Phase" => c.ColPhase,
        "Outcome" => c.ColOutcome,
        "Notes" => c.ColNotes,
        _ => false,
    };

    private static void Set(Configuration c, string name, bool v)
    {
        switch (name)
        {
            case "Login": c.ColLogin = v; break;
            case "Tp": c.ColTp = v; break;
            case "Character": c.ColCharacter = v; break;
            case "Region": c.ColRegion = v; break;
            case "District": c.ColDistrict = v; break;
            case "WardPlot": c.ColWardPlot = v; break;
            case "Size": c.ColSize = v; break;
            case "Type": c.ColType = v; break;
            case "EntryNumber": c.ColEntryNumber = v; break;
            case "EntryDate": c.ColEntryDate = v; break;
            case "ResultsDate": c.ColResultsDate = v; break;
            case "Countdown": c.ColCountdown = v; break;
            case "Phase": c.ColPhase = v; break;
            case "Outcome": c.ColOutcome = v; break;
            case "Notes": c.ColNotes = v; break;
        }
    }

    public override void Draw()
    {
        var cfg = plugin.Config;

        ImGui.TextDisabled("Capture is passive: open a housing placard during an entry or results period and the bid is recorded automatically.");
        ImGui.Separator();

        if (ImGui.CollapsingHeader("Columns", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // Build the effective order: saved order first, then any unsaved appended.
            var order = new List<string>();
            foreach (var n in cfg.ColumnOrder)
                if (Array.Exists(columns, x => x.Name == n) && !order.Contains(n)) order.Add(n);
            foreach (var (_, n) in columns)
                if (!order.Contains(n)) order.Add(n);

            var moved = false;
            for (var i = 0; i < order.Count && !moved; i++)
            {
                var name = order[i];
                var label = Array.Find(columns, x => x.Name == name).Label;
                ImGui.PushID($"col{i}");

                var atTop = i == 0;
                var atBottom = i == order.Count - 1;

                if (atTop) ImGui.BeginDisabled();
                if (ImGui.ArrowButton("up", ImGuiDir.Up) && !atTop)
                {
                    (order[i - 1], order[i]) = (order[i], order[i - 1]);
                    cfg.ColumnOrder = new List<string>(order);
                    cfg.Save();
                    moved = true;
                }
                if (atTop) ImGui.EndDisabled();

                ImGui.SameLine();
                if (atBottom) ImGui.BeginDisabled();
                if (ImGui.ArrowButton("down", ImGuiDir.Down) && !atBottom)
                {
                    (order[i + 1], order[i]) = (order[i], order[i + 1]);
                    cfg.ColumnOrder = new List<string>(order);
                    cfg.Save();
                    moved = true;
                }
                if (atBottom) ImGui.EndDisabled();

                ImGui.SameLine();
                var on = Get(cfg, name);
                if (ImGui.Checkbox(label, ref on))
                {
                    Set(cfg, name, on);
                    cfg.Save();
                }

                ImGui.PopID();
            }

            var manual = cfg.ManualColumnResize;
            if (ImGui.Checkbox("Allow manual column resize (drag borders)", ref manual))
            {
                cfg.ManualColumnResize = manual;
                cfg.Save();
            }
        }

        if (ImGui.CollapsingHeader("View"))
        {
            var hide = cfg.HidePastCycles;
            if (ImGui.Checkbox("Hide finished bids (lost / claim window closed)", ref hide))
            {
                cfg.HidePastCycles = hide;
                cfg.Save();
            }
        }

        if (ImGui.CollapsingHeader("Accounts"))
        {
            var showAcc = cfg.ShowAccountColumn;
            if (ImGui.Checkbox("Show Account column", ref showAcc))
            {
                cfg.ShowAccountColumn = showAcc;
                cfg.Save();
            }
            ImGui.TextDisabled("Name each account below. Accounts are identified automatically as you record bids on different installs.");

            // Gather the distinct account keys seen across recorded bids.
            var keys = new List<string>();
            foreach (var b in plugin.Config.Bids)
            {
                var k = b.AccountKey ?? string.Empty;
                if (!string.IsNullOrEmpty(k) && !keys.Contains(k)) keys.Add(k);
            }
            // Include any keys that already have aliases but no current bids.
            foreach (var k in cfg.AccountAliases.Keys)
                if (!keys.Contains(k)) keys.Add(k);

            if (keys.Count == 0)
            {
                ImGui.TextDisabled("No accounts seen yet.");
            }
            else
            {
                foreach (var key in keys)
                {
                    cfg.AccountAliases.TryGetValue(key, out var alias);
                    alias ??= string.Empty;
                    var tail = key.LastIndexOfAny(new[] { '/', '\\' }) is var idx && idx >= 0 && idx < key.Length - 1
                        ? key[(idx + 1)..] : key;

                    ImGui.PushID($"acct{key}");
                    ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputTextWithHint("##alias", tail, ref alias, 64))
                    {
                        if (string.IsNullOrWhiteSpace(alias)) cfg.AccountAliases.Remove(key);
                        else cfg.AccountAliases[key] = alias;
                        cfg.Save();
                    }
                    ImGui.SameLine();
                    ImGui.TextDisabled(tail);
                    ImGui.PopID();
                }
            }
        }

        if (ImGui.CollapsingHeader("Storage"))
        {
            var shared = cfg.UseSharedStorage;
            if (ImGui.Checkbox("Shared storage (all clients on this PC share one bid list)", ref shared))
            {
                cfg.UseSharedStorage = shared;
                cfg.Save();
                plugin.InitStore();
            }
            ImGui.TextDisabled("Recommended for multiboxing — survives --roamingPath differences.");

            if (cfg.UseSharedStorage)
            {
                var path = cfg.SharedStoragePathOverride;
                ImGui.SetNextItemWidth(360f * ImGuiHelpers.GlobalScale);
                if (ImGui.InputTextWithHint("##sharedpath", "(default %APPDATA%\\XIVLauncher\\pluginConfigs\\HousingLottoTracker\\shared.json)", ref path, 512))
                    cfg.SharedStoragePathOverride = path;
                ImGui.SameLine();
                if (ImGui.Button("Apply path"))
                {
                    cfg.Save();
                    plugin.InitStore();
                }
            }
        }

        if (ImGui.CollapsingHeader("Alerts"))
            DrawAlerts(cfg);

        ImGui.Separator();
        if (!confirmClear)
        {
            if (ImGui.Button("Clear ALL recorded bids")) confirmClear = true;
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "Delete every recorded bid? This cannot be undone.");
            if (ImGui.Button("Yes, clear everything"))
            {
                plugin.ClearAll();
                confirmClear = false;
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) confirmClear = false;
        }
    }

    // ---- Alerts UI ----
    private string editingRuleId = "";

    private void DrawAlerts(Configuration cfg)
    {
        var enabled = cfg.AlertsEnabled;
        if (ImGui.Checkbox("Enable open-plot alerts (data from PaissaDB)", ref enabled))
        {
            cfg.AlertsEnabled = enabled;
            cfg.Save();
        }
        ImGui.TextDisabled("Crowd-sourced from PaissaDB (zhu.codes). Coverage depends on other players reporting plots, same as the PaissaHouse plugin.");

        var loginOnly = cfg.AlertOnLoginOnly;
        if (ImGui.Checkbox("Only pop up at login (otherwise pops up whenever a new match appears)", ref loginOnly))
        {
            cfg.AlertOnLoginOnly = loginOnly;
            cfg.Save();
        }

        var poll = cfg.AlertPollSeconds;
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Check interval (seconds)", ref poll))
        {
            cfg.AlertPollSeconds = Math.Max(30, poll);
            cfg.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Watch rules:");

        AlertRule? toDelete = null;
        foreach (var rule in cfg.AlertRules)
        {
            ImGui.PushID(rule.Id);
            var on = rule.Enabled;
            if (ImGui.Checkbox("##en", ref on)) { rule.Enabled = on; cfg.Save(); }
            ImGui.SameLine();
            ImGui.TextUnformatted(rule.Describe());
            ImGui.SameLine();
            if (ImGui.SmallButton(editingRuleId == rule.Id ? "Done" : "Edit"))
                editingRuleId = editingRuleId == rule.Id ? "" : rule.Id;
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete")) toDelete = rule;

            if (editingRuleId == rule.Id)
                DrawRuleEditor(cfg, rule);

            ImGui.PopID();
        }

        if (toDelete != null)
        {
            cfg.AlertRules.Remove(toDelete);
            cfg.Save();
        }

        if (ImGui.Button("Add watch rule"))
        {
            var r = new AlertRule { DistrictId = 641 }; // default Shirogane
            cfg.AlertRules.Add(r);
            editingRuleId = r.Id;
            cfg.Save();
        }
    }

    private void DrawRuleEditor(Configuration cfg, AlertRule rule)
    {
        ImGui.Indent();

        // Label.
        var label = rule.Label;
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputTextWithHint("Label (optional)", "auto-described if blank", ref label, 64))
        { rule.Label = label; cfg.Save(); }

        // District (single select; 0 = any).
        var districts = Game.PlacardReader.AllDistricts;
        var curDistrict = rule.DistrictId == 0 ? "Any district" : Game.PlacardReader.DistrictDisplayName(rule.DistrictId);
        ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("District", curDistrict))
        {
            if (ImGui.Selectable("Any district", rule.DistrictId == 0)) { rule.DistrictId = 0; cfg.Save(); }
            foreach (var (name, id) in districts)
                if (ImGui.Selectable(name, rule.DistrictId == id)) { rule.DistrictId = id; cfg.Save(); }
            ImGui.EndCombo();
        }

        // Plots and wards: comma-separated number inputs (easiest for multi-entry).
        DrawIntListInput(cfg, "Plots (e.g. 60 or 31-60 — blank = any)", rule.Plots);
        DrawIntListInput(cfg, "Wards (e.g. 26 or 1-30 — blank = any)", rule.Wards);

        // Sizes (multi).
        ImGui.TextUnformatted("Sizes (none = any):");
        ImGui.SameLine();
        foreach (var sz in new[] { LottoPlotSize.Small, LottoPlotSize.Medium, LottoPlotSize.Large })
        {
            var has = rule.Sizes.Contains(sz);
            if (ImGui.Checkbox($"{sz}##sz", ref has))
            {
                if (has && !rule.Sizes.Contains(sz)) rule.Sizes.Add(sz);
                else rule.Sizes.RemoveAll(x => x == sz);
                cfg.Save();
            }
            ImGui.SameLine();
        }
        ImGuiHelpers.ScaledDummy(1f);

        // Scope: regions / DCs / worlds. We show region + DC + world multiselects.
        DrawScopeSelectors(cfg, rule);

        ImGui.Unindent();
        ImGui.Separator();
    }

    private void DrawIntListInput(Configuration cfg, string label, List<int> target)
    {
        // Display the stored numbers collapsed into compact ranges (e.g. "5, 31-60").
        var text = CollapseToRanges(target);
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText(label, ref text, 128))
        {
            target.Clear();
            foreach (var n in ExpandRanges(text))
                if (n > 0 && !target.Contains(n)) target.Add(n);
            target.Sort();
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Accepts single numbers and ranges, e.g. \"5, 31-60\". Blank = any.");
    }

    // Parse "5, 31-60, 12" into the full set of integers.
    private static IEnumerable<int> ExpandRanges(string text)
    {
        foreach (var part in text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = part.Trim();
            var dash = seg.IndexOf('-');
            if (dash > 0 && dash < seg.Length - 1)
            {
                if (int.TryParse(seg[..dash].Trim(), out var lo) &&
                    int.TryParse(seg[(dash + 1)..].Trim(), out var hi))
                {
                    if (lo > hi) (lo, hi) = (hi, lo);
                    // guard against absurd ranges
                    if (hi - lo <= 1000)
                        for (var i = lo; i <= hi; i++) yield return i;
                }
            }
            else if (int.TryParse(seg, out var n))
            {
                yield return n;
            }
        }
    }

    // Collapse a sorted set of integers back into compact ranges for display.
    private static string CollapseToRanges(List<int> nums)
    {
        if (nums.Count == 0) return string.Empty;
        var sorted = nums.Distinct().OrderBy(x => x).ToList();
        var parts = new List<string>();
        int start = sorted[0], prev = sorted[0];
        for (var i = 1; i <= sorted.Count; i++)
        {
            if (i < sorted.Count && sorted[i] == prev + 1)
            {
                prev = sorted[i];
                continue;
            }
            parts.Add(start == prev ? $"{start}" : $"{start}-{prev}");
            if (i < sorted.Count) { start = sorted[i]; prev = sorted[i]; }
        }
        return string.Join(", ", parts);
    }

    private void DrawScopeSelectors(Configuration cfg, AlertRule rule)
    {
        var worlds = plugin.Alerts?.Worlds ?? new();
        var regions = worlds.Select(w => w.Region).Distinct().OrderBy(x => x).ToList();
        var dcs = worlds.Select(w => w.DataCenter).Distinct().OrderBy(x => x).ToList();

        // Regions.
        if (ImGui.TreeNode($"Regions ({(rule.Regions.Count == 0 ? "any" : string.Join(",", rule.Regions))})###rgn"))
        {
            foreach (var r in regions)
            {
                var has = rule.Regions.Contains(r);
                if (ImGui.Checkbox($"{r}##rgn", ref has))
                {
                    if (has) rule.Regions.Add(r); else rule.Regions.Remove(r);
                    cfg.Save();
                }
            }
            ImGui.TreePop();
        }

        // Data centers.
        if (ImGui.TreeNode($"Data centers ({(rule.DataCenters.Count == 0 ? "any" : string.Join(",", rule.DataCenters))})###dc"))
        {
            foreach (var d in dcs)
            {
                var has = rule.DataCenters.Contains(d);
                if (ImGui.Checkbox($"{d}##dc", ref has))
                {
                    if (has) rule.DataCenters.Add(d); else rule.DataCenters.Remove(d);
                    cfg.Save();
                }
            }
            ImGui.TreePop();
        }

        // Worlds (grouped by DC for sanity).
        if (ImGui.TreeNode($"Worlds ({(rule.Worlds.Count == 0 ? "any" : string.Join(",", rule.Worlds))})###wld"))
        {
            foreach (var dc in dcs)
            {
                ImGui.TextDisabled(dc);
                foreach (var w in worlds.Where(x => x.DataCenter == dc).OrderBy(x => x.World))
                {
                    var has = rule.Worlds.Contains(w.World);
                    if (ImGui.Checkbox($"{w.World}##wld", ref has))
                    {
                        if (has) rule.Worlds.Add(w.World); else rule.Worlds.Remove(w.World);
                        cfg.Save();
                    }
                }
            }
            ImGui.TreePop();
        }
    }
}
