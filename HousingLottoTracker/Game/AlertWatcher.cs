using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using HousingLottoTracker.Data;

namespace HousingLottoTracker.Game;

// Polls PaissaDB for open lottery plots that match the user's alert rules. Tracks
// which matches have been acknowledged ("Seen") so a given open plot only alerts
// once until it fills and re-opens. Runs its polling off the framework thread.
public sealed class AlertWatcher : IDisposable
{
    private readonly Configuration cfg;
    private readonly PaissaClient client;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Action saveConfig;

    // World metadata cache (name/dc/region by world id), built once from Lumina.
    private List<(string World, ushort WorldId, string DataCenter, string Region)> worldTable = new();

    // Currently-active (unacknowledged) alerts, keyed by AckKey.
    private readonly Dictionary<string, ActiveAlert> active = new();
    private readonly object gate = new();

    // Keys of plots seen open in the most recent successful poll, to expire acks.
    private HashSet<string> lastOpenPlotKeys = new();

    private DateTime lastPoll = DateTime.MinValue;
    private bool polling;

    public AlertWatcher(Configuration cfg, PaissaClient client, IDataManager data, IPluginLog log, Action saveConfig)
    {
        this.cfg = cfg;
        this.client = client;
        this.data = data;
        this.log = log;
        this.saveConfig = saveConfig;
    }

    public void Dispose() { }

    public void EnsureWorldTable()
    {
        if (worldTable.Count == 0)
            worldTable = PlacardReader.EnumerateWorlds(data);
    }

    public List<(string World, ushort WorldId, string DataCenter, string Region)> Worlds
    {
        get { EnsureWorldTable(); return worldTable; }
    }

    // Snapshot of active alerts for the UI.
    public List<ActiveAlert> ActiveAlerts()
    {
        lock (gate) return active.Values.OrderBy(a => a.FirstSeenUtc).ToList();
    }

    public int ActiveCount { get { lock (gate) return active.Count; } }

    // Acknowledge one alert: record its key so it won't re-fire until the plot
    // re-opens, and remove it from the active set.
    public void Acknowledge(string ackKey)
    {
        lock (gate)
        {
            active.Remove(ackKey);
        }
        if (!cfg.AcknowledgedAlerts.Contains(ackKey))
        {
            cfg.AcknowledgedAlerts.Add(ackKey);
            saveConfig();
        }
    }

    public void AcknowledgeAll()
    {
        lock (gate)
        {
            foreach (var k in active.Keys.ToList())
                if (!cfg.AcknowledgedAlerts.Contains(k)) cfg.AcknowledgedAlerts.Add(k);
            active.Clear();
        }
        saveConfig();
    }

    // Called from the framework tick. Kicks off an async poll when due.
    public void Tick()
    {
        if (!cfg.AlertsEnabled) return;
        if (cfg.AlertRules.Count == 0) return;
        if (polling) return;
        if (DateTime.UtcNow - lastPoll < TimeSpan.FromSeconds(Math.Max(30, cfg.AlertPollSeconds))) return;

        lastPoll = DateTime.UtcNow;
        polling = true;
        _ = Task.Run(PollAsync);
    }

    private async Task PollAsync()
    {
        try
        {
            EnsureWorldTable();
            var rules = cfg.AlertRules.Where(r => r.Enabled).ToList();
            if (rules.Count == 0) return;

            // Determine the (world, district) pairs we actually need to query, from
            // the union of all enabled rules. This keeps request counts bounded.
            var targets = BuildTargets(rules);
            if (targets.Count == 0) return;

            var allOpen = new List<OpenPlot>();
            foreach (var (wid, wname, dc, region, districtId) in targets)
            {
                var plots = await client.GetOpenPlotsAsync(wid, wname, dc, region, districtId);
                allOpen.AddRange(plots);
            }

            var openKeys = new HashSet<string>(allOpen.Select(p => p.Key));

            // Expire acknowledgements for plots that are no longer open, so they can
            // re-alert when they next become available.
            var changed = false;
            if (cfg.AcknowledgedAlerts.Count > 0)
            {
                var stillValid = new List<string>();
                foreach (var ack in cfg.AcknowledgedAlerts)
                {
                    // ack key = ruleId:worldId:districtId:ward:plot ; the plot part is
                    // the trailing 4 segments after the first ':'.
                    var firstColon = ack.IndexOf(':');
                    var plotKey = firstColon >= 0 ? ack[(firstColon + 1)..] : ack;
                    if (openKeys.Contains(plotKey)) stillValid.Add(ack);
                    else changed = true;
                }
                if (changed)
                {
                    cfg.AcknowledgedAlerts = stillValid;
                    saveConfig();
                }
            }

            // Match open plots against rules; add new (unacknowledged) ones.
            lock (gate)
            {
                foreach (var plot in allOpen)
                {
                    foreach (var rule in rules)
                    {
                        if (!rule.Matches(plot)) continue;
                        var ackKey = $"{rule.Id}:{plot.Key}";
                        if (cfg.AcknowledgedAlerts.Contains(ackKey)) continue;
                        if (active.ContainsKey(ackKey)) continue;

                        active[ackKey] = new ActiveAlert
                        {
                            RuleId = rule.Id,
                            RuleLabel = rule.Describe(),
                            Plot = plot,
                        };
                        log.Info($"Housing Lotto Tracker alert: {plot.District} W{plot.Ward} P{plot.Plot} ({plot.WorldName}) matched \"{rule.Describe()}\"");
                    }
                }
            }

            lastOpenPlotKeys = openKeys;
        }
        catch (Exception ex)
        {
            log.Error(ex, "alert poll failed");
        }
        finally
        {
            polling = false;
        }
    }

    // Build the distinct (world, district) query set from the rules' filters.
    private List<(ushort, string, string, string, ushort)> BuildTargets(List<AlertRule> rules)
    {
        EnsureWorldTable();
        var targets = new List<(ushort, string, string, string, ushort)>();
        var seen = new HashSet<string>();

        foreach (var rule in rules)
        {
            // Which worlds does this rule cover?
            IEnumerable<(string World, ushort WorldId, string DataCenter, string Region)> worlds = worldTable;
            if (rule.Worlds.Count > 0)
                worlds = worlds.Where(w => rule.Worlds.Contains(w.World, StringComparer.OrdinalIgnoreCase));
            else if (rule.DataCenters.Count > 0)
                worlds = worlds.Where(w => rule.DataCenters.Contains(w.DataCenter, StringComparer.OrdinalIgnoreCase));
            else if (rule.Regions.Count > 0)
                worlds = worlds.Where(w => rule.Regions.Contains(w.Region, StringComparer.OrdinalIgnoreCase));

            // Which districts?
            var districts = rule.DistrictId != 0
                ? new[] { rule.DistrictId }
                : new ushort[] { 339, 340, 341, 641, 979 };

            foreach (var w in worlds)
            {
                foreach (var d in districts)
                {
                    var key = $"{w.WorldId}:{d}";
                    if (seen.Add(key))
                        targets.Add((w.WorldId, w.World, w.DataCenter, w.Region, d));
                }
            }
        }

        return targets;
    }
}
