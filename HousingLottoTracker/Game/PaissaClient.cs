using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using HousingLottoTracker.Data;
using Newtonsoft.Json;

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
        public byte purchase_system { get; set; } // 1=Lottery, 2=FC, 4=Individual (flags)
    }

    // Fetch open plots for one world + district. Returns an empty list on any error
    // (network down, world has no data, etc.) so the caller degrades gracefully.
    public async Task<List<OpenPlot>> GetOpenPlotsAsync(
        ushort worldId, string worldName, string dataCenter, string region, ushort districtId)
    {
        var outList = new List<OpenPlot>();
        try
        {
            var resp = await http.GetAsync($"{ApiBase}/worlds/{worldId}/{districtId}");
            if (!resp.IsSuccessStatusCode) return outList;

            var json = await resp.Content.ReadAsStringAsync();
            var detail = JsonConvert.DeserializeObject<DistrictDetail>(json);
            if (detail?.open_plots == null) return outList;

            foreach (var op in detail.open_plots)
            {
                // purchase_system is a flag set; lottery bit = 1.
                var isLottery = (op.purchase_system & 1) != 0;

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
                });
            }
        }
        catch { /* network/parse error -> empty */ }
        return outList;
    }
}
