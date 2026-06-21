using System;
using System.Collections.Generic;
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
        ("Character", "Character"),
        ("Region", "Region"),
        ("District", "District"),
        ("Ward/Plot", "WardPlot"),
        ("Size", "Size"),
        ("Type (FC/Personal)", "Type"),
        ("Entrants", "Entrants"),
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
        "Character" => c.ColCharacter,
        "Region" => c.ColRegion,
        "District" => c.ColDistrict,
        "WardPlot" => c.ColWardPlot,
        "Size" => c.ColSize,
        "Type" => c.ColType,
        "Entrants" => c.ColEntrants,
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
            case "Character": c.ColCharacter = v; break;
            case "Region": c.ColRegion = v; break;
            case "District": c.ColDistrict = v; break;
            case "WardPlot": c.ColWardPlot = v; break;
            case "Size": c.ColSize = v; break;
            case "Type": c.ColType = v; break;
            case "Entrants": c.ColEntrants = v; break;
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
}
