using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using HousingLottoTracker.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HousingLottoTracker.Game;

// Queries PaissaDB (https://paissadb.zhu.codes) — the same crowd-sourced housing
// database PaissaHouse uses — for currently-open lottery plots. We poll the public
// GET endpoint per (world, district); no auth or WebSocket required.
//
// Endpoint: GET /worlds/{worldId}/{districtId} -> DistrictDetail
public sealed class PaissaClient : IDisposable
{
    private const string ApiBase = "https://paissadb.zhu.codes";
    private readonly HttpClient http;

    public PaissaClient()
    {
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HousingLottoTracker/1.0 (+dalamud)");
    }

    public void Dispose() => http.Dispose();

    // --- response DTOs (subset of PaissaDB's schema) ---
    private sealed class DistrictDetail
    {
        public ushort district_id { get; set; }
        public string? name { get; set; }
        public ushort num_open_plots { get; set; }
        public OpenPlotDetail[]? open_plots { get; set; }
    }

    private sealed class OpenPlotDetail
    {
        public ushort world_id { get; set; }
        public ushort district_id { get; set; }
        public ushort ward_number { get; set; }   // 0-based from the API
        public ushort plot_number { get; set; }   // 0-based from the API
        public ushort size { get; set; }          // 0 small, 1 medium, 2 large
        public uint price { get; set; }

        // PaissaDB may serialise this as a number (flags: 1=Lottery, 2=FC, 4=Individual)
        // or as a string ("Lottery"). Accept either.
        [JsonProperty("purchase_system")]
        public JToken? purchase_system { get; set; }

        public uint? lotto_entries { get; set; }
        public uint? lotto_phase_until { get; set; }

        // Lottery phase: 1 = Available (entries open), 2 = InResultsPeriod,
        // 3 = Unavailable. May arrive as a number or a string.
        [JsonProperty("lotto_phase")]
        public JToken? lotto_phase { get; set; }

        // -1 = unknown/absent.
        public int PhaseValue()
        {
            if (lotto_phase == null || lotto_phase.Type == JTokenType.Null) return -1;
            try
            {
                if (lotto_phase.Type == JTokenType.Integer)
                    return lotto_phase.Value<int>();

                var s = lotto_phase.Value<string>() ?? string.Empty;
                if (s.IndexOf("resultsperiod", StringComparison.OrdinalIgnoreCase) >= 0
                    || s.IndexOf("results", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
                if (s.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
                if (s.IndexOf("available", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
                if (int.TryParse(s, out var n)) return n;
            }
            catch { /* fall through */ }
            return -1;
        }

        // True when the lottery is actually accepting entries. If the API doesn't
        // report a phase at all, we don't exclude the plot (treat unknown as open).
        public bool EntriesOpen()
        {
            var p = PhaseValue();
            return p == -1 || p == 1;
        }

        // True when this plot is sold via lottery, tolerant of either encoding.
        public bool IsLottery()
        {
            if (purchase_system == null) return false;
            try
            {
                if (purchase_system.Type == JTokenType.Integer)
                    return (purchase_system.Value<int>() & 1) != 0;

                var s = purchase_system.Value<string>() ?? string.Empty;
                if (s.IndexOf("lottery", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                // Some encoders emit the numeric flags as a string.
                if (int.TryParse(s, out var n)) return (n & 1) != 0;
            }
            catch { /* fall through */ }
            return false;
        }
    }

    // Optional diagnostic sink (set by the plugin) so failures/oddities are visible.
    public Action<string>? Debug;

    // Fetch open plots for one world + district. Returns an empty list on any error
    // (network down, world has no data, etc.) so the caller degrades gracefully.
    public async Task<List<OpenPlot>> GetOpenPlotsAsync(
        ushort worldId, string worldName, string dataCenter, string region, ushort districtId)
    {
        var outList = new List<OpenPlot>();
        try
        {
            var resp = await http.GetAsync($"{ApiBase}/worlds/{worldId}/{districtId}");
            if (!resp.IsSuccessStatusCode)
            {
                Debug?.Invoke($"PaissaDB {worldName}/{districtId}: HTTP {(int)resp.StatusCode}");
                return outList;
            }

            var json = await resp.Content.ReadAsStringAsync();
            var detail = JsonConvert.DeserializeObject<DistrictDetail>(json);
            if (detail?.open_plots == null)
            {
                Debug?.Invoke($"PaissaDB {worldName}/{districtId}: no open_plots in response");
                return outList;
            }

            var lotteryCount = 0;
            var openForEntryCount = 0;
            foreach (var op in detail.open_plots)
            {
                var isLottery = op.IsLottery();
                var phase = op.PhaseValue();
                if (isLottery) lotteryCount++;
                if (isLottery && op.EntriesOpen()) openForEntryCount++;

                outList.Add(new OpenPlot
                {
                    WorldId = op.world_id,
                    WorldName = worldName,
                    DataCenter = dataCenter,
                    Region = region,
                    DistrictId = op.district_id,
                    District = PlacardReader.DistrictDisplayName(op.district_id),
                    Ward = op.ward_number + 1,    // to 1-based
                    Plot = op.plot_number + 1,
                    Size = op.size switch
                    {
                        0 => LottoPlotSize.Small,
                        1 => LottoPlotSize.Medium,
                        2 => LottoPlotSize.Large,
                        _ => LottoPlotSize.Unknown,
                    },
                    IsLottery = isLottery,
                    LottoPhaseValue = phase,
                });
            }

            Debug?.Invoke($"PaissaDB {worldName}/{PlacardReader.DistrictDisplayName(districtId)}: {detail.open_plots.Length} open, {lotteryCount} lottery, {openForEntryCount} accepting entries");
        }
        catch (Exception ex)
        {
            Debug?.Invoke($"PaissaDB {worldName}/{districtId} failed: {ex.Message}");
        }
        return outList;
    }
}
