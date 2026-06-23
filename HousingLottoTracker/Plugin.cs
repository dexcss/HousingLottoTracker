using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using HousingLottoTracker.Data;
using HousingLottoTracker.Game;
using HousingLottoTracker.Windows;

namespace HousingLottoTracker;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider InteropProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/hlt";
    private const string CommandAlias = "/lotto";

    public Configuration Config { get; }
    public SharedStore? Store { get; private set; }
    private readonly WindowSystem windowSystem = new("HousingLottoTracker");
    private readonly MainWindow mainWindow;
    private readonly SettingsWindow settingsWindow;
    private PlacardSaleHook? saleHook;

    public string AccountKey { get; }

    private DateTime lastPoll = DateTime.MinValue;
    private DateTime lastSharedRefresh = DateTime.MinValue;
    private DateTime lastOutcomeTick = DateTime.MinValue;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);          // placard read cadence
    private static readonly TimeSpan SharedRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OutcomeTickInterval = TimeSpan.FromMinutes(5);

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(PluginInterface);

        AccountKey = ComputeAccountKey();
        InitStore();

        mainWindow = new MainWindow(this);
        settingsWindow = new SettingsWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(settingsWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Housing Lotto Tracker window.",
        });
        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Housing Lotto Tracker window (alias for /hlt).",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        Framework.Update += OnUpdate;
        ClientState.Login += OnLogin;

        // Win auto-detect: the results placard congratulates you via a SelectYesno.
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesno);

        // Primary capture: the chat confirmation emitted when you submit a bid.
        ChatGui.ChatMessage += OnChatMessage;

        // Route placard node-text dumps to the Dalamud log for diagnostics.
        PlacardReader.DebugLog = msg => Log.Debug(msg);

        // Reliable primary capture: hook the placard sale-info function. If the
        // signature can't be found (e.g. after a patch), this no-ops and the
        // text-scrape path in Poll() keeps working.
        saleHook = new PlacardSaleHook(InteropProvider, Log, OnPlacardSaleInfo);
    }

    public void Dispose()
    {
        ChatGui.ChatMessage -= OnChatMessage;
        PlacardReader.DebugLog = null;
        saleHook?.Dispose();
        saleHook = null;
        AddonLifecycle.UnregisterListener(OnSelectYesno);
        Framework.Update -= OnUpdate;
        ClientState.Login -= OnLogin;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        windowSystem.RemoveAllWindows();
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);
    }

    private static string ComputeAccountKey()
    {
        try
        {
            var dir = PluginInterface.GetPluginConfigDirectory();
            var pluginConfigs = System.IO.Directory.GetParent(dir)?.FullName ?? dir;
            var roamingRoot = System.IO.Directory.GetParent(pluginConfigs)?.FullName ?? pluginConfigs;
            return roamingRoot;
        }
        catch
        {
            return "default";
        }
    }

    public void OpenSettings() => settingsWindow.IsOpen = true;
    private void OpenMain() => mainWindow.IsOpen = true;
    private void OnCommand(string command, string args) => mainWindow.Toggle();

    public void InitStore()
    {
        if (Config.UseSharedStorage)
        {
            Store = new SharedStore(true, Config.SharedStoragePathOverride);
            Config.Bids = Store.LoadAll();
            Log.Info($"Housing Lotto Tracker using shared storage: {Store.Path_}");
        }
        else
        {
            Store = null;
        }
        lastSharedRefresh = DateTime.UtcNow;
    }

    public void PersistBid(BidRecord record)
    {
        if (Store != null) Store.UpsertBid(record);
        else Config.Save();
    }

    public void DeleteBid(string key)
    {
        Config.Bids.RemoveAll(x => x.Key == key);
        if (Store != null)
        {
            Store.DeleteBid(key);
            Config.Bids = Store.LoadAll();
        }
        else
        {
            Config.Save();
        }
    }

    public void ClearAll()
    {
        Config.Bids.Clear();
        if (Store != null)
        {
            Store.ClearAll();
            Config.Bids = Store.LoadAll();
        }
        else
        {
            Config.Save();
        }
    }

    // Add a user-entered bid. Returns "" on success or an error message. If a bid
    // with the same key already exists, it's updated rather than duplicated.
    public string AddManualBid(BidRecord rec)
    {
        if (rec.ContentId == 0) return "No character — log in, or the bid can't be tied to anyone.";
        if (rec.Ward == 0 || rec.Plot == 0) return "Ward and plot are required.";

        var existing = Config.Bids.FindIndex(x => x.Key == rec.Key);
        if (existing >= 0) Config.Bids[existing] = rec;
        else Config.Bids.Add(rec);

        PersistBid(rec);
        if (Store != null) Config.Bids = Store.LoadAll();
        return "";
    }

    private void OnLogin() => lastPoll = DateTime.MinValue;

    private void OnUpdate(IFramework framework)
    {
        if (!ClientState.IsLoggedIn) return;

        // Periodically re-read the shared file so this client sees other clients' bids.
        if (Store != null && DateTime.UtcNow - lastSharedRefresh >= SharedRefreshInterval)
        {
            lastSharedRefresh = DateTime.UtcNow;
            try { Config.Bids = Store.LoadAll(); }
            catch (Exception ex) { Log.Error(ex, "shared refresh failed"); }
        }

        // Advance stale outcomes (claim windows that simply elapsed).
        if (DateTime.UtcNow - lastOutcomeTick >= OutcomeTickInterval)
        {
            lastOutcomeTick = DateTime.UtcNow;
            try
            {
                if (BidStore.TickOutcomes(Config.Bids))
                    foreach (var b in Config.Bids) PersistBid(b);
            }
            catch (Exception ex) { Log.Error(ex, "outcome tick failed"); }
        }

        if (DateTime.UtcNow - lastPoll < PollInterval) return;
        lastPoll = DateTime.UtcNow;

        try { Poll(); }
        catch (Exception ex) { Log.Error(ex, "poll failed"); }
    }

    private void Poll()
    {
        var local = ObjectTable.LocalPlayer;
        if (local == null) return;

        var contentId = PlayerState.ContentId;
        if (contentId == 0) return;

        var name = local.Name.TextValue;
        var world = local.HomeWorld.Value.Name.ExtractText();
        var region = PlacardReader.ResolveRegionCode(DataManager, world);

        // Backfill source: the "Housing Lottery Status" popup (Duty > Timers >
        // Estate). Available any time during the entry period, so it captures bids
        // placed before the plugin was installed, and tells us FC-vs-personal.
        TryCaptureFromStatus(contentId, name, world, region);

        // Primary live source: the placard ("For Sale" window) when open.
        if (!PlacardReader.IsPlacardOpen(GameGui)) return;

        var snap = PlacardReader.Read(GameGui, DataManager, (ushort)ClientState.TerritoryType);
        if (!snap.Valid) return;

        var rec = BidStore.CaptureFromPlacard(
            Config.Bids, snap, contentId, name, world, region, AccountKey);

        if (rec != null)
            PersistBid(rec);
    }

    private unsafe void TryCaptureFromStatus(ulong contentId, string name, string world, string region)
    {
        var text = PlacardReader.FindLotteryStatusText();
        if (string.IsNullOrEmpty(text)) return;

        var parsed = LotteryStatusParser.Parse(text);
        if (!parsed.HasEntry) return;

        var territoryId = PlacardReader.DistrictNameToTerritoryId(parsed.District);
        if (territoryId == 0) territoryId = (ushort)ClientState.TerritoryType;

        var rec = BidStore.CaptureFromStatus(
            Config.Bids, parsed, territoryId, contentId, name, world, region, AccountKey);

        if (rec != null)
        {
            if (rec.Size == LottoPlotSize.Unknown)
                rec.Size = PlacardReader.ResolvePlotSize(DataManager, rec.TerritoryTypeId, rec.Plot);
            PersistBid(rec);
        }
    }

    // Reliable primary capture from the placard sale-info hook. Updates an existing
    // bid for the viewed plot with the exact deadline and FC flag. Does not create
    // bids (the hook fires for any placard you look at) — creation stays with the
    // chat confirmation and the Timers status panel.
    private void OnPlacardSaleInfo(PlacardSaleHook.SaleInfo info)
    {
        try
        {
            var contentId = PlayerState.ContentId;
            if (contentId == 0) return;

            var local = ObjectTable.LocalPlayer;
            var name = local?.Name.TextValue ?? string.Empty;
            var world = local?.HomeWorld.Value.Name.ExtractText() ?? string.Empty;
            var region = PlacardReader.ResolveRegionCode(DataManager, world);
            var district = PlacardReader.ResolveDistrict(DataManager, info.TerritoryTypeId);

            var rec = BidStore.CaptureFromSaleInfo(
                Config.Bids, info, district, contentId, name, world, region, AccountKey);

            if (rec != null)
            {
                if (rec.Size == LottoPlotSize.Unknown)
                    rec.Size = PlacardReader.ResolvePlotSize(DataManager, rec.TerritoryTypeId, rec.Plot);
                PersistBid(rec);
                Log.Info($"Housing Lotto Tracker: hook updated {rec.LocationText} ({rec.TypeText}) results {rec.ResultsAvailableUtc:yyyy-MM-dd HH:mm}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "placard sale-info capture failed");
        }
    }

    // Primary capture path: parse the lottery entry confirmation from chat.
    // API15's ChatMessage uses OnHandleableChatMessageDelegate(IHandleableChatMessage).
    private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage chat)
    {
        try
        {
            var text = chat.Message.TextValue;
            if (string.IsNullOrEmpty(text) || !text.Contains("lottery", StringComparison.OrdinalIgnoreCase))
                return;

            var parsed = ChatLotteryParser.Parse(text);
            if (!parsed.IsEntry || parsed.Plot <= 0 || parsed.Ward <= 0)
                return;

            var contentId = PlayerState.ContentId;
            if (contentId == 0) return;

            var local = ObjectTable.LocalPlayer;
            var name = local?.Name.TextValue ?? string.Empty;
            var world = local?.HomeWorld.Value.Name.ExtractText() ?? string.Empty;
            var region = PlacardReader.ResolveRegionCode(DataManager, world);

            // Resolve district name -> territory id; fall back to the current zone.
            var territoryId = PlacardReader.DistrictNameToTerritoryId(parsed.District);
            if (territoryId == 0) territoryId = (ushort)ClientState.TerritoryType;

            // Size isn't in the chat line, but it's derivable from the sheet by
            // territory + plot number, so it fills in immediately at bid time.
            var size = PlacardReader.ResolvePlotSize(DataManager, territoryId, parsed.Plot);

            var rec = BidStore.CaptureFromChat(
                Config.Bids, parsed, territoryId, contentId, name, world, region,
                AccountKey, isFreeCompany: false, size);

            PersistBid(rec);
            Log.Info($"Housing Lotto Tracker: recorded bid {rec.LocationText} #{rec.EntryNumber} for {rec.CharacterDisplay}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "chat capture failed");
        }
    }

    // Win auto-detect via the results placard's confirmation prompt. We flip the
    // matching this-cycle bid for the current character to Won.
    private void OnSelectYesno(AddonEvent type, AddonArgs args)
    {
        try
        {
            var text = PlacardReader.ReadSelectYesnoPrompt(args.Addon);
            if (string.IsNullOrEmpty(text)) return;

            var lower = text.ToLowerInvariant();
            var winWord = lower.Contains("congratulations") || lower.Contains("won the lottery")
                          || lower.Contains("winning");
            var housingWord = lower.Contains("plot") || lower.Contains("estate")
                              || lower.Contains("house") || lower.Contains("land")
                              || lower.Contains("residential");

            var signboardOpen = GameGui.GetAddonByName("HousingSignBoard", 1) != nint.Zero;

            if (!winWord) return;
            if (!housingWord && !signboardOpen) return;

            var contentId = PlayerState.ContentId;
            if (contentId == 0) return;

            // Prefer a bid in the current cycle; else the most recent pending bid for
            // this character.
            var cycleId = LottoCycle.CycleIdFor(DateTime.UtcNow);
            BidRecord? rec = Config.Bids.Find(b =>
                b.ContentId == contentId && b.EntryCycleId == cycleId &&
                b.Outcome == LottoOutcome.Pending);

            if (rec == null)
            {
                BidRecord? best = null;
                foreach (var b in Config.Bids)
                {
                    if (b.ContentId != contentId) continue;
                    if (b.Outcome != LottoOutcome.Pending) continue;
                    if (best == null || b.EntryDateUtc > best.EntryDateUtc) best = b;
                }
                rec = best;
            }

            if (rec == null) return;

            rec.Outcome = LottoOutcome.Won;
            rec.OutcomeRecordedUtc = DateTime.UtcNow;
            PersistBid(rec);
            Log.Info($"Housing Lotto Tracker: auto-recorded WIN for {rec.CharacterDisplay} on {rec.LocationText}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "win auto-detect failed");
        }
    }
}
