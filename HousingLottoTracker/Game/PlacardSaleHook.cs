using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;

namespace HousingLottoTracker.Game;

// Hooks the game's "handle placard sale info" function, which fires whenever you
// view a housing placard. This delivers a structured PlacardSaleInfo with the exact
// phase-end timestamp, tenant type (FC vs personal), availability phase, and the
// plot location as function arguments — far more reliable than scraping addon text.
//
// Approach adapted from PaissaHouse (AutoSweep). The signature can break on a game
// patch; when that happens the hook simply doesn't install and the text-scrape
// fallback in PlacardReader keeps working until the signature is updated.
public sealed unsafe class PlacardSaleHook : IDisposable
{
    public sealed class SaleInfo
    {
        public ushort TerritoryTypeId;
        public byte WardId;       // 0-based as the game passes it
        public byte PlotId;       // 0-based
        public short ApartmentNumber;
        public HousingKind HousingType;
        public PurchaseKind PurchaseType;
        public TenantKind TenantType;
        public AvailabilityKind Availability;
        public uint PhaseEndsAtUnix;   // Unix seconds; for lottery this is the entry deadline
        public uint EntryCount;        // entrants (not stored by us, but available)

        public bool IsLottery => PurchaseType == PurchaseKind.Lottery;
        public bool IsFreeCompany => TenantType == TenantKind.FreeCompany;
        public bool InResults => Availability == AvailabilityKind.InResultsPeriod;

        public DateTime? PhaseEndsAtUtc =>
            PhaseEndsAtUnix == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(PhaseEndsAtUnix).UtcDateTime;
    }

    public enum HousingKind : byte { OwnedHouse = 0, UnownedHouse = 1, FreeCompanyApartment = 2, Apartment = 3 }
    public enum PurchaseKind : byte { Unavailable = 0, FCFS = 1, Lottery = 2 }
    public enum TenantKind : byte { FreeCompany = 1, Personal = 2 }
    public enum AvailabilityKind : byte { Available = 1, InResultsPeriod = 2, Unavailable = 3 }

    private delegate void HandlePlacardSaleInfoDelegate(
        void* agentBase, byte housingType, ushort territoryTypeId, byte wardId, byte plotId,
        short apartmentNumber, IntPtr placardSaleInfoPtr, long a8);

    private Hook<HandlePlacardSaleInfoDelegate>? hook;

    // Movups pattern inside the function body; the function start is at -0xA8.
    [Signature("41 0F 10 06 0F 11 43 48 41 0F 10 4E 10 0F 11 4B 58")]
    private IntPtr movupsAddress;

    private readonly IPluginLog log;
    private readonly Action<SaleInfo> onSale;

    public bool Installed { get; private set; }

    public PlacardSaleHook(IGameInteropProvider interop, IPluginLog log, Action<SaleInfo> onSale)
    {
        this.log = log;
        this.onSale = onSale;

        try
        {
            interop.InitializeFromAttributes(this);
            if (movupsAddress != IntPtr.Zero)
            {
                var functionStart = movupsAddress - 0xA8;
                hook = interop.HookFromAddress<HandlePlacardSaleInfoDelegate>(functionStart, Detour);
                hook.Enable();
                Installed = true;
                log.Info("Housing Lotto Tracker: placard sale-info hook installed.");
            }
            else
            {
                log.Warning("Housing Lotto Tracker: placard sale-info signature not found; using text-scrape fallback.");
            }
        }
        catch (Exception ex)
        {
            log.Warning($"Housing Lotto Tracker: placard hook failed to install ({ex.Message}); using text-scrape fallback.");
        }
    }

    public void Dispose()
    {
        try { hook?.Dispose(); } catch { /* ignore */ }
        hook = null;
        Installed = false;
    }

    private void Detour(
        void* agentBase, byte housingType, ushort territoryTypeId, byte wardId, byte plotId,
        short apartmentNumber, IntPtr placardSaleInfoPtr, long a8)
    {
        hook!.Original(agentBase, housingType, territoryTypeId, wardId, plotId, apartmentNumber, placardSaleInfoPtr, a8);

        try
        {
            if (placardSaleInfoPtr == IntPtr.Zero) return;

            var info = new SaleInfo
            {
                TerritoryTypeId = territoryTypeId,
                WardId = wardId,
                PlotId = plotId,
                ApartmentNumber = apartmentNumber,
                HousingType = (HousingKind)housingType,
            };

            // PlacardSaleInfo layout (offsets within the struct):
            //   0x00 PurchaseType (byte)
            //   0x01 TenantType (byte)
            //   0x02 AvailabilityType (byte)
            //   0x08 PhaseEndsAt (uint, unix seconds)
            //   0x10 EntryCount (uint)
            var p = (byte*)placardSaleInfoPtr;
            info.PurchaseType = (PurchaseKind)p[0x00];
            info.TenantType = (TenantKind)p[0x01];
            info.Availability = (AvailabilityKind)p[0x02];
            info.PhaseEndsAtUnix = *(uint*)(p + 0x08);
            info.EntryCount = *(uint*)(p + 0x10);

            onSale(info);
        }
        catch (Exception ex)
        {
            log.Error(ex, "placard sale-info detour failed");
        }
    }
}
