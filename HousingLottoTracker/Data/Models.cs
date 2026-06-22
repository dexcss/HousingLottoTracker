using System;
using System.Collections.Generic;

namespace HousingLottoTracker.Data;

// Lottery phase as read from the placard.
public enum LottoPhase
{
    Unknown = 0,
    Entry = 1,    // accepting bids
    Results = 2,  // winners being decided / claimable
}

// Outcome of a bid once results are known.
public enum LottoOutcome
{
    Pending = 0,  // results not yet checked / not yet available
    Won = 1,      // your number was drawn
    Lost = 2,     // refunded
    Claimed = 3,  // won AND you finalized the purchase
    Expired = 4,  // results window passed without claim
}

// Plot size mirrors PlotSize in ClientStructs (Small=0, Medium=1, Large=2). 5 == apartment.
public enum LottoPlotSize : byte
{
    Small = 0,
    Medium = 1,
    Large = 2,
    Apartment = 5,
    Unknown = 255,
}

// A single housing lottery bid placed by one character on one plot in one cycle.
// Keyed by (ContentId + TerritoryTypeId + Ward + Plot + EntryCycleId) so the same
// character re-bidding the same plot in a later cycle is a distinct row.
public class BidRecord
{
    // --- Identity ---
    public ulong ContentId;
    public string CharacterName = string.Empty;
    public string WorldName = string.Empty;
    public string Region = string.Empty;           // JP/NA/EU/OCE/CN/KR/CLD/TCN

    // The Dalamud roaming path this character was last seen under (account axis).
    public string AccountKey = string.Empty;

    // --- Plot location ---
    public ushort TerritoryTypeId;
    public string District = string.Empty;          // Mist / Lavender Beds / etc.
    public byte Ward;                                // 1-based for display
    public byte Plot;                                // 1-based for display
    public LottoPlotSize Size = LottoPlotSize.Unknown;
    public bool IsFreeCompany;                       // true = bid for FC, false = personal

    // --- Lottery state (captured from the placard) ---
    public LottoPhase Phase = LottoPhase.Unknown;

    // Your personal ticket number. The game shows this transiently at bid time and
    // does NOT persist it, so it's auto-grabbed when visible and otherwise editable.
    public int EntryNumber = -1;                     // -1 = unknown / not captured
    public int WinningNumber = -1;                   // shown on the results placard

    // --- Timing ---
    // When we first recorded this bid (best proxy for the actual bid time).
    public DateTime EntryDateUtc = DateTime.UtcNow;
    // The cycle the bid belongs to; derived from the global 9-day lottery loop so two
    // bids in the same window collapse to one row even if recorded on different days.
    public long EntryCycleId;

    // Results open 5 days after the entry period starts for that plot. We can only
    // approximate from EntryDateUtc unless the placard tells us a precise time.
    public DateTime? ResultsAvailableUtc;            // when results phase begins
    public DateTime? ClaimDeadlineUtc;               // results + 4 days

    // --- Outcome ---
    public LottoOutcome Outcome = LottoOutcome.Pending;
    public DateTime? OutcomeRecordedUtc;

    // --- Bookkeeping ---
    public DateTime LastSeenUtc = DateTime.UtcNow;
    public string Source = "Live";                   // Live / manual
    public string Notes = string.Empty;

    // Stable key used by the UI and the shared store for upsert/merge.
    public string Key => $"{ContentId}:{TerritoryTypeId}:{Ward}:{Plot}:{EntryCycleId}";

    // --- Derived display helpers ---
    public string SizeText => Size switch
    {
        LottoPlotSize.Small => "Small",
        LottoPlotSize.Medium => "Medium",
        LottoPlotSize.Large => "Large",
        LottoPlotSize.Apartment => "Apartment",
        _ => "?",
    };

    public string TypeText => IsFreeCompany ? "FC" : "Personal";

    public string PhaseText => Phase switch
    {
        LottoPhase.Entry => "Entry",
        LottoPhase.Results => "Results",
        _ => "?",
    };

    public string OutcomeText => Outcome switch
    {
        LottoOutcome.Won => "Won",
        LottoOutcome.Lost => "Lost",
        LottoOutcome.Claimed => "Claimed",
        LottoOutcome.Expired => "Expired",
        _ => "Pending",
    };

    public string LocationText
    {
        get
        {
            var d = string.IsNullOrEmpty(District) ? $"T{TerritoryTypeId}" : District;
            return $"{d} W{Ward} P{Plot}";
        }
    }

    public string CharacterDisplay => string.IsNullOrEmpty(WorldName)
        ? CharacterName
        : $"{CharacterName} @ {WorldName}";

    // Time until results open (>= zero) or zero if already open/unknown.
    public TimeSpan TimeUntilResults
    {
        get
        {
            if (ResultsAvailableUtc == null) return TimeSpan.Zero;
            var d = ResultsAvailableUtc.Value - DateTime.UtcNow;
            return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }
    }

    // Time until the claim/refund window closes (>= zero) once in results.
    public TimeSpan TimeUntilClaimDeadline
    {
        get
        {
            if (ClaimDeadlineUtc == null) return TimeSpan.Zero;
            var d = ClaimDeadlineUtc.Value - DateTime.UtcNow;
            return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }
    }

    public bool ResultsOpen => ResultsAvailableUtc != null && DateTime.UtcNow >= ResultsAvailableUtc.Value;
}
