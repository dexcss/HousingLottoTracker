using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using HousingLottoTracker.Game;

namespace HousingLottoTracker.Windows;

// Popup listing currently-matched open plots. Auto-opens when new alerts arrive;
// hitting "Seen" acknowledges that plot so it won't re-alert until it re-opens.
public class AlertWindow : Window
{
    private readonly Plugin plugin;
    private readonly AlertWatcher watcher;

    public AlertWindow(Plugin plugin, AlertWatcher watcher)
        : base("Housing Alert!###HousingLottoAlert",
               ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.plugin = plugin;
        this.watcher = watcher;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 120) * ImGuiHelpers.GlobalScale,
            MaximumSize = new Vector2(900, 900),
        };
    }

    private string status = "";
    private DateTime statusUntil = DateTime.MinValue;

    public override void Draw()
    {
        var alerts = watcher.ActiveAlerts();

        // ---- Controls (always shown, even with no alerts) ----
        DrawControls();
        ImGui.Separator();

        if (alerts.Count == 0)
        {
            ImGui.TextDisabled(watcher.IsPolling
                ? "Checking PaissaDB..."
                : "No matching plots are open right now.");
            if (watcher.LastPollUtc != DateTime.MinValue)
                ImGui.TextDisabled($"Last checked: {watcher.LastPollUtc.ToLocalTime():HH:mm:ss}");
            if (ImGui.Button("Close")) IsOpen = false;
            return;
        }

        ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f),
            alerts.Count == 1 ? "A plot you're watching is available!" : $"{alerts.Count} watched plots are available!");
        ImGui.Separator();

        foreach (var a in alerts)
        {
            ImGui.PushID(a.AckKey);
            var p = a.Plot;
            ImGui.TextUnformatted($"{p.District} Ward {p.Ward}, Plot {p.Plot}  ({p.SizeText})");
            ImGui.TextDisabled($"{p.WorldName} · {p.DataCenter} · {p.Region} · {p.PhaseText}");
            ImGui.TextDisabled($"matches: {a.RuleLabel}");

            if (ImGui.Button($"Seen###seen{a.AckKey}"))
                watcher.Acknowledge(a.AckKey);
            ImGui.SameLine();
            if (PluginIpc.IsLifestreamAvailable() && ImGui.Button($"Travel###tp{a.AckKey}"))
            {
                PluginIpc.LifestreamGoToAddress(p.WorldName, p.District, p.Ward, p.Plot);
            }

            ImGui.Separator();
            ImGui.PopID();
        }

        if (ImGui.Button("Seen all"))
        {
            watcher.AcknowledgeAll();
            IsOpen = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Close")) IsOpen = false;
    }

    private void DrawControls()
    {
        var busy = watcher.IsPolling;

        if (busy) ImGui.BeginDisabled();
        if (ImGui.Button("Re-check now"))
        {
            SetStatus(watcher.ForcePoll()
                ? "Checking PaissaDB..."
                : "Nothing to check — add a watch rule in Settings.");
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear seen & re-check"))
        {
            SetStatus(watcher.ClearSeenAndRecheck()
                ? "Cleared acknowledgements — re-checking..."
                : "Cleared acknowledgements. Add a watch rule in Settings to check.");
        }
        if (busy) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Settings")) plugin.OpenSettings();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Manage your watch rules.");

        if (busy)
            ImGui.TextDisabled("Checking PaissaDB...");
        else if (!string.IsNullOrEmpty(status) && DateTime.UtcNow < statusUntil)
            ImGui.TextDisabled(status);
        else if (watcher.LastPollUtc != DateTime.MinValue)
            ImGui.TextDisabled($"Last checked: {watcher.LastPollUtc.ToLocalTime():HH:mm:ss}");
    }

    private void SetStatus(string s)
    {
        status = s;
        statusUntil = DateTime.UtcNow.AddSeconds(6);
    }
}
