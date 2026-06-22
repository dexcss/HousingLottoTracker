using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using HousingLottoTracker.Data;
using HousingLottoTracker.Game;

namespace HousingLottoTracker.Windows;

public class MainWindow : Window
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("Housing Lotto Tracker###HousingLottoMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 320) * ImGuiHelpers.GlobalScale,
            MaximumSize = new Vector2(4000, 4000),
        };
    }

    private string search = string.Empty;
    private string expandedKey = "";
    private string pendingDeleteKey = "";
    private bool pendingClearAll;

    // ---- Manual add form state ----
    private bool showAddForm;
    private int mDistrictIdx;          // index into PlacardReader.Districts
    private int mWard = 1;
    private int mPlot = 1;
    private int mSizeIdx;              // 0 Small, 1 Medium, 2 Large
    private bool mIsFc;
    private int mEntryNumber = -1;
    private string mEntryDate = DateTime.Now.ToString("yyyy-MM-dd");
    private string mCharName = "";
    private string mWorldName = "";
    private string mNotes = "";
    private string mAddResult = "";

    // ---- Sorting ----
    private enum SortCol { Character, Region, District, WardPlot, Size, Type, EntryNumber, EntryDate, ResultsDate, Countdown, Phase, Outcome }
    private SortCol sort = SortCol.EntryDate;
    private bool sortAsc = false; // newest first by default

    // Column identity, mirroring the config toggles.
    private enum Col { Character, Region, District, WardPlot, Size, Type, EntryNumber, EntryDate, ResultsDate, Countdown, Phase, Outcome, Notes }

    private List<Col> EnabledColumns(Configuration c)
    {
        var list = new List<Col>();
        if (c.ColCharacter) list.Add(Col.Character);
        if (c.ColRegion) list.Add(Col.Region);
        if (c.ColDistrict) list.Add(Col.District);
        if (c.ColWardPlot) list.Add(Col.WardPlot);
        if (c.ColSize) list.Add(Col.Size);
        if (c.ColType) list.Add(Col.Type);
        if (c.ColEntryNumber) list.Add(Col.EntryNumber);
        if (c.ColEntryDate) list.Add(Col.EntryDate);
        if (c.ColResultsDate) list.Add(Col.ResultsDate);
        if (c.ColCountdown) list.Add(Col.Countdown);
        if (c.ColPhase) list.Add(Col.Phase);
        if (c.ColOutcome) list.Add(Col.Outcome);
        if (c.ColNotes) list.Add(Col.Notes);

        // Apply saved order if present.
        if (c.ColumnOrder.Count > 0)
        {
            var order = c.ColumnOrder
                .Select(n => Enum.TryParse<Col>(n, out var col) ? (Col?)col : null)
                .Where(x => x.HasValue).Select(x => x!.Value).ToList();
            list = list.OrderBy(col =>
            {
                var i = order.IndexOf(col);
                return i < 0 ? int.MaxValue : i;
            }).ToList();
        }

        return list;
    }

    private static string HeaderFor(Col c) => c switch
    {
        Col.Character => "Character",
        Col.Region => "Region",
        Col.District => "District",
        Col.WardPlot => "Ward/Plot",
        Col.Size => "Size",
        Col.Type => "Type",
        Col.EntryNumber => "Your #",
        Col.EntryDate => "Entered",
        Col.ResultsDate => "Results",
        Col.Countdown => "Countdown",
        Col.Phase => "Phase",
        Col.Outcome => "Outcome",
        Col.Notes => "Notes",
        _ => "",
    };

    private static SortCol? SortForCol(Col c) => c switch
    {
        Col.Character => SortCol.Character,
        Col.Region => SortCol.Region,
        Col.District => SortCol.District,
        Col.WardPlot => SortCol.WardPlot,
        Col.Size => SortCol.Size,
        Col.Type => SortCol.Type,
        Col.EntryNumber => SortCol.EntryNumber,
        Col.EntryDate => SortCol.EntryDate,
        Col.ResultsDate => SortCol.ResultsDate,
        Col.Countdown => SortCol.Countdown,
        Col.Phase => SortCol.Phase,
        Col.Outcome => SortCol.Outcome,
        _ => null,
    };

    public override void Draw()
    {
        var cfg = plugin.Config;

        // ---- Top bar ----
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##search", "Search character / district / world...", ref search, 64);
        ImGui.SameLine();
        if (ImGui.Button(showAddForm ? "Close add form" : "Add bid")) showAddForm = !showAddForm;
        ImGui.SameLine();
        if (ImGui.Button("Settings")) plugin.OpenSettings();
        ImGui.SameLine();
        var hidePast = cfg.HidePastCycles;
        if (ImGui.Checkbox("Hide finished", ref hidePast))
        {
            cfg.HidePastCycles = hidePast;
            cfg.Save();
        }

        if (showAddForm)
        {
            ImGui.Separator();
            DrawAddForm();
        }

        ImGui.Separator();

        var bids = FilterAndSort(cfg);
        if (bids.Count == 0)
        {
            ImGui.TextDisabled("No bids recorded yet. Click a housing placard during an entry or results period — or use \"Add bid\" to enter one manually.");
            DrawDeferred();
            return;
        }

        DrawTable(bids, cfg);
        DrawDeferred();
    }

    private void DrawAddForm()
    {
        var s = ImGuiHelpers.GlobalScale;

        // Default character identity to the logged-in character (editable, so alts
        // can be backfilled too).
        var local = Plugin.ObjectTable.LocalPlayer;
        var liveName = local?.Name.TextValue ?? "";
        var liveWorld = local?.HomeWorld.Value.Name.ExtractText() ?? "";

        if (string.IsNullOrEmpty(mCharName) && !string.IsNullOrEmpty(liveName)) mCharName = liveName;
        if (string.IsNullOrEmpty(mWorldName) && !string.IsNullOrEmpty(liveWorld)) mWorldName = liveWorld;

        ImGui.TextDisabled("Backfill a bid the plugin didn't capture. Ties to the logged-in character by default.");

        // District dropdown.
        var districts = PlacardReader.Districts;
        ImGui.SetNextItemWidth(180f * s);
        var districtLabel = (mDistrictIdx >= 0 && mDistrictIdx < districts.Length) ? districts[mDistrictIdx].Name : "Select...";
        if (ImGui.BeginCombo("District", districtLabel))
        {
            for (var i = 0; i < districts.Length; i++)
            {
                if (ImGui.Selectable(districts[i].Name, i == mDistrictIdx)) mDistrictIdx = i;
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f * s);
        ImGui.InputInt("Ward", ref mWard);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f * s);
        ImGui.InputInt("Plot", ref mPlot);

        string[] sizes = { "Small", "Medium", "Large" };
        ImGui.SetNextItemWidth(120f * s);
        if (ImGui.BeginCombo("Size", sizes[Math.Clamp(mSizeIdx, 0, 2)]))
        {
            for (var i = 0; i < sizes.Length; i++)
            {
                if (ImGui.Selectable(sizes[i], i == mSizeIdx)) mSizeIdx = i;
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.Checkbox("FC bid", ref mIsFc);

        ImGui.SetNextItemWidth(140f * s);
        ImGui.InputText("Entry date (yyyy-MM-dd)", ref mEntryDate, 10);

        ImGui.SetNextItemWidth(110f * s);
        ImGui.InputInt("Your number", ref mEntryNumber);

        ImGui.SetNextItemWidth(160f * s);
        ImGui.InputText("Character", ref mCharName, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140f * s);
        ImGui.InputText("World", ref mWorldName, 64);

        ImGui.SetNextItemWidth(360f * s);
        ImGui.InputText("Notes##add", ref mNotes, 256);

        if (ImGui.Button("Save bid"))
            SubmitManualBid();
        ImGui.SameLine();
        if (ImGui.Button("Reset form")) ResetAddForm();

        if (!string.IsNullOrEmpty(mAddResult))
        {
            ImGui.SameLine();
            var ok = mAddResult == "Saved.";
            ImGui.TextColored(ok ? new Vector4(0.4f, 1f, 0.5f, 1f) : new Vector4(1f, 0.6f, 0.4f, 1f), mAddResult);
        }
    }

    private void SubmitManualBid()
    {
        var districts = PlacardReader.Districts;
        if (mDistrictIdx < 0 || mDistrictIdx >= districts.Length) { mAddResult = "Pick a district."; return; }
        var (districtName, territoryId) = districts[mDistrictIdx];

        if (mWard < 1 || mPlot < 1) { mAddResult = "Ward and plot must be 1 or higher."; return; }

        if (!DateTime.TryParse(mEntryDate, out var entryLocal))
        {
            mAddResult = "Entry date must be yyyy-MM-dd.";
            return;
        }
        var entryUtc = DateTime.SpecifyKind(entryLocal, DateTimeKind.Local).ToUniversalTime();

        var contentId = Plugin.PlayerState.ContentId;
        if (contentId == 0)
        {
            mAddResult = "Log in first so the bid can be tied to a character.";
            return;
        }

        var region = PlacardReader.ResolveRegionCode(Plugin.DataManager, mWorldName);
        var size = (LottoPlotSize)(byte)mSizeIdx;

        var rec = BidStore.CreateManual(
            contentId, mCharName, mWorldName, region, plugin.AccountKey,
            territoryId, districtName, (byte)mWard, (byte)mPlot,
            size, mIsFc, entryUtc, mEntryNumber, mNotes);

        var err = plugin.AddManualBid(rec);
        mAddResult = string.IsNullOrEmpty(err) ? "Saved." : err;
    }

    private void ResetAddForm()
    {
        mDistrictIdx = 0; mWard = 1; mPlot = 1; mSizeIdx = 0; mIsFc = false;
        mEntryNumber = -1;
        mEntryDate = DateTime.Now.ToString("yyyy-MM-dd");
        mCharName = ""; mWorldName = ""; mNotes = ""; mAddResult = "";
    }

    private List<BidRecord> FilterAndSort(Configuration cfg)
    {
        IEnumerable<BidRecord> q = cfg.Bids;

        if (cfg.HidePastCycles)
            q = q.Where(b => b.Outcome == LottoOutcome.Pending
                             || b.Outcome == LottoOutcome.Won
                             || (b.ClaimDeadlineUtc != null && DateTime.UtcNow <= b.ClaimDeadlineUtc.Value));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(b =>
                b.CharacterName.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                b.WorldName.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                b.District.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                b.Region.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        var list = q.ToList();

        Comparison<BidRecord> cmp = sort switch
        {
            SortCol.Character => (a, b) => string.Compare(a.CharacterDisplay, b.CharacterDisplay, StringComparison.OrdinalIgnoreCase),
            SortCol.Region => (a, b) => string.Compare(a.Region, b.Region, StringComparison.OrdinalIgnoreCase),
            SortCol.District => (a, b) => string.Compare(a.District, b.District, StringComparison.OrdinalIgnoreCase),
            SortCol.WardPlot => (a, b) => a.Ward != b.Ward ? a.Ward.CompareTo(b.Ward) : a.Plot.CompareTo(b.Plot),
            SortCol.Size => (a, b) => ((byte)a.Size).CompareTo((byte)b.Size),
            SortCol.Type => (a, b) => a.IsFreeCompany.CompareTo(b.IsFreeCompany),
            SortCol.EntryNumber => (a, b) => a.EntryNumber.CompareTo(b.EntryNumber),
            SortCol.EntryDate => (a, b) => a.EntryDateUtc.CompareTo(b.EntryDateUtc),
            SortCol.ResultsDate => (a, b) => Nullable.Compare(a.ResultsAvailableUtc, b.ResultsAvailableUtc),
            SortCol.Countdown => (a, b) => a.TimeUntilResults.CompareTo(b.TimeUntilResults),
            SortCol.Phase => (a, b) => ((int)a.Phase).CompareTo((int)b.Phase),
            SortCol.Outcome => (a, b) => ((int)a.Outcome).CompareTo((int)b.Outcome),
            _ => (a, b) => 0,
        };
        list.Sort(cmp);
        if (!sortAsc) list.Reverse();
        return list;
    }

    private void DrawTable(List<BidRecord> bids, Configuration cfg)
    {
        var columns = EnabledColumns(cfg);
        if (columns.Count == 0)
        {
            ImGui.TextDisabled("All columns are hidden — enable some in Settings.");
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH
                    | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY
                    | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings;
        if (cfg.ManualColumnResize) flags |= ImGuiTableFlags.Resizable;

        // Reserve the lower half of the window for the detail panel when expanded.
        var detailOpen = !string.IsNullOrEmpty(expandedKey) && bids.Exists(x => x.Key == expandedKey);
        var outerSize = new Vector2(0, detailOpen ? ImGui.GetContentRegionAvail().Y * 0.5f : 0f);

        if (ImGui.BeginTable("##bidtable", columns.Count, flags, outerSize))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            foreach (var c in columns)
                ImGui.TableSetupColumn(HeaderFor(c), ImGuiTableColumnFlags.WidthStretch, WidthWeight(c));

            // Header row — sequential TableNextColumn keeps counts in lockstep.
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            foreach (var c in columns)
            {
                ImGui.TableNextColumn();
                var sc = SortForCol(c);
                var label = HeaderFor(c);
                var arrow = (sc.HasValue && sort == sc.Value) ? (sortAsc ? " \u25B2" : " \u25BC") : "";
                if (sc.HasValue)
                {
                    if (ImGui.Selectable($"{label}{arrow}###hdr{(int)c}"))
                    {
                        if (sort == sc.Value) sortAsc = !sortAsc;
                        else { sort = sc.Value; sortAsc = true; }
                    }
                }
                else
                {
                    ImGui.TextDisabled(label);
                }
            }

            foreach (var b in bids)
                DrawRow(b, columns);

            ImGui.EndTable();
        }

        // Detail panel below the table (avoids in-cell clipping).
        if (!string.IsNullOrEmpty(expandedKey))
        {
            var open = bids.Find(x => x.Key == expandedKey);
            if (open != null)
            {
                ImGuiHelpers.ScaledDummy(4f);
                ImGui.Separator();
                if (ImGui.BeginChild("##biddetail", new Vector2(0, 0), false))
                    DrawExpanded(open);
                ImGui.EndChild();
            }
        }
    }

    private static float WidthWeight(Col c) => c switch
    {
        Col.Character => 1.8f,
        Col.Region => 0.6f,
        Col.District => 1.3f,
        Col.WardPlot => 0.8f,
        Col.Size => 0.7f,
        Col.Type => 0.7f,
        Col.EntryNumber => 0.6f,
        Col.EntryDate => 1.0f,
        Col.ResultsDate => 1.0f,
        Col.Countdown => 1.2f,
        Col.Phase => 0.7f,
        Col.Outcome => 0.8f,
        Col.Notes => 1.4f,
        _ => 1.0f,
    };

    private void DrawRow(BidRecord b, List<Col> columns)
    {
        ImGui.PushID(b.Key);
        ImGui.TableNextRow();

        var isOpen = expandedKey == b.Key;
        var tri = isOpen ? "\u25BC " : "\u25B6 ";

        for (var ci = 0; ci < columns.Count; ci++)
        {
            ImGui.TableNextColumn();
            if (ci == 0)
            {
                // First cell doubles as the row's expand toggle (spans all columns).
                var label = CellText(b, columns[0]);
                if (ImGui.Selectable($"{tri}{label}###row{b.Key}", isOpen, ImGuiSelectableFlags.SpanAllColumns))
                    expandedKey = isOpen ? "" : b.Key;
            }
            else
            {
                DrawCell(b, columns[ci]);
            }
        }

        ImGui.PopID();
    }

    // Plain-text form of a cell (used for the first, click-to-expand column).
    private static string CellText(BidRecord b, Col c) => c switch
    {
        Col.Character => b.CharacterDisplay,
        Col.Region => b.Region,
        Col.District => string.IsNullOrEmpty(b.District) ? "?" : b.District,
        Col.WardPlot => $"W{b.Ward} P{b.Plot}",
        Col.Size => b.SizeText,
        Col.Type => b.TypeText,
        Col.EntryNumber => b.EntryNumber < 0 ? "—" : b.EntryNumber.ToString(),
        Col.EntryDate => b.EntryDateUtc.ToLocalTime().ToString("yyyy-MM-dd"),
        Col.ResultsDate => b.ResultsAvailableUtc?.ToLocalTime().ToString("yyyy-MM-dd") ?? "?",
        Col.Countdown => CountdownText(b),
        Col.Phase => b.PhaseText,
        Col.Outcome => b.OutcomeText,
        Col.Notes => b.Notes,
        _ => "",
    };

    private static string CountdownText(BidRecord b)
    {
        if (b.Outcome is LottoOutcome.Lost or LottoOutcome.Claimed or LottoOutcome.Expired) return "—";
        if (!b.ResultsOpen) return $"results in {Fmt(b.TimeUntilResults)}";
        var t = b.TimeUntilClaimDeadline;
        return t <= TimeSpan.Zero ? "window closed" : $"claim in {Fmt(t)}";
    }

    private void DrawCell(BidRecord b, Col c)
    {
        switch (c)
        {
            case Col.Character: ImGui.TextUnformatted(b.CharacterDisplay); break;
            case Col.Region: ImGui.TextUnformatted(b.Region); break;
            case Col.District: ImGui.TextUnformatted(string.IsNullOrEmpty(b.District) ? "?" : b.District); break;
            case Col.WardPlot: ImGui.TextUnformatted($"W{b.Ward} P{b.Plot}"); break;
            case Col.Size: ImGui.TextUnformatted(b.SizeText); break;
            case Col.Type: ImGui.TextUnformatted(b.TypeText); break;
            case Col.EntryNumber: ImGui.TextUnformatted(b.EntryNumber < 0 ? "—" : b.EntryNumber.ToString()); break;
            case Col.EntryDate: ImGui.TextUnformatted(b.EntryDateUtc.ToLocalTime().ToString("yyyy-MM-dd")); break;
            case Col.ResultsDate:
                ImGui.TextUnformatted(b.ResultsAvailableUtc?.ToLocalTime().ToString("yyyy-MM-dd") ?? "?");
                break;
            case Col.Countdown: DrawCountdown(b); break;
            case Col.Phase: ImGui.TextUnformatted(b.PhaseText); break;
            case Col.Outcome: DrawOutcome(b); break;
            case Col.Notes: ImGui.TextUnformatted(b.Notes); break;
        }
    }

    private void DrawCountdown(BidRecord b)
    {
        if (b.Outcome is LottoOutcome.Lost or LottoOutcome.Claimed or LottoOutcome.Expired)
        {
            ImGui.TextDisabled("—");
            return;
        }

        if (!b.ResultsOpen)
        {
            var t = b.TimeUntilResults;
            ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), $"results in {Fmt(t)}");
        }
        else
        {
            var t = b.TimeUntilClaimDeadline;
            if (t <= TimeSpan.Zero) ImGui.TextDisabled("window closed");
            else ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f), $"claim in {Fmt(t)}");
        }
    }

    private void DrawOutcome(BidRecord b)
    {
        var col = b.Outcome switch
        {
            LottoOutcome.Won => new Vector4(0.4f, 1f, 0.5f, 1f),
            LottoOutcome.Claimed => new Vector4(0.4f, 1f, 0.5f, 1f),
            LottoOutcome.Lost => new Vector4(1f, 0.5f, 0.5f, 1f),
            LottoOutcome.Expired => new Vector4(0.8f, 0.5f, 0.5f, 1f),
            _ => new Vector4(0.8f, 0.8f, 0.8f, 1f),
        };
        ImGui.TextColored(col, b.OutcomeText);
    }

    private void DrawExpanded(BidRecord b)
    {
        ImGui.Indent();
        ImGui.TextDisabled($"{b.CharacterDisplay}  ·  {b.LocationText}  ·  {b.TypeText}  ·  {b.SizeText}");

        // Editable entry number (the game doesn't persist it; let the user set it).
        var num = b.EntryNumber;
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Your entry number", ref num))
        {
            b.EntryNumber = num < 0 ? -1 : num;
            // Re-evaluate win/loss if we now know both numbers.
            if (b.Outcome == LottoOutcome.Pending && b.WinningNumber >= 0 && b.EntryNumber >= 0)
            {
                b.Outcome = b.WinningNumber == b.EntryNumber ? LottoOutcome.Won : LottoOutcome.Lost;
                b.OutcomeRecordedUtc = DateTime.UtcNow;
            }
            plugin.PersistBid(b);
        }

        if (b.WinningNumber >= 0)
            ImGui.TextUnformatted($"Winning number: {b.WinningNumber}");

        // Outcome override.
        ImGui.TextUnformatted("Outcome:");
        var outcomes = new[] { LottoOutcome.Pending, LottoOutcome.Won, LottoOutcome.Lost, LottoOutcome.Claimed };
        for (var oi = 0; oi < outcomes.Length; oi++)
        {
            var oc = outcomes[oi];
            ImGui.SameLine();
            if (ImGui.RadioButton(oc.ToString() + $"##oc{b.Key}", b.Outcome == oc))
            {
                b.Outcome = oc;
                b.OutcomeRecordedUtc = DateTime.UtcNow;
                plugin.PersistBid(b);
            }
        }

        // Notes.
        var notes = b.Notes;
        ImGui.SetNextItemWidth(360f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText($"Notes##{b.Key}", ref notes, 256))
        {
            b.Notes = notes;
        }
        ImGui.SameLine();
        if (ImGui.Button($"Save##notes{b.Key}")) plugin.PersistBid(b);

        // Timing detail.
        if (b.ResultsAvailableUtc != null)
            ImGui.TextDisabled($"Results open: {b.ResultsAvailableUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}   ·   Claim/refund by: {b.ClaimDeadlineUtc?.ToLocalTime():yyyy-MM-dd HH:mm}");
        ImGui.TextDisabled($"Last seen: {b.LastSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm}");

        if (ImGui.Button($"Delete this bid##{b.Key}"))
            pendingDeleteKey = b.Key;

        ImGui.Unindent();
    }

    private static string Fmt(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{t.Minutes}m";
    }

    private void DrawDeferred()
    {
        if (!string.IsNullOrEmpty(pendingDeleteKey))
        {
            plugin.DeleteBid(pendingDeleteKey);
            pendingDeleteKey = "";
        }
        if (pendingClearAll)
        {
            plugin.ClearAll();
            pendingClearAll = false;
        }
    }
}
