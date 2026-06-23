using System;
using System.Collections.Generic;
using HousingLottoTracker.Game;

namespace HousingLottoTracker.Data;

// Coordinates capture: takes a placard snapshot for the current character and
// upserts the matching BidRecord, filling timing from the cycle clock. Kept
// separate from Plugin so the capture rules are easy to read and test.
public static class BidStore
{
    // Build/merge a bid from a placard read. Returns the resulting record, or null
    // if there wasn't enough to identify a plot.
    public static BidRecord? CaptureFromPlacard(
        List<BidRecord> bids,
        PlacardSnapshot snap,
        ulong contentId,
        string charName,
        string worldName,
        string region,
        string accountKey)
    {
        if (!snap.Valid || contentId == 0) return null;
        if (snap.Ward == 0 || snap.Plot == 0) return null;

        // Which cycle does "now" belong to? During entry, this is the bidding cycle.
        // During results, the bid we're checking belongs to the SAME cycle (results
        // run within the same 9-day block as the entry that opened it).
        var now = DateTime.UtcNow;
        var cycleId = LottoCycle.CycleIdFor(now);

        // Find an existing bid for this char/plot in this cycle.
        var keyMatch = bids.Find(b =>
            b.ContentId == contentId &&
            b.TerritoryTypeId == snap.TerritoryTypeId &&
            b.Ward == snap.Ward &&
            b.Plot == snap.Plot &&
            b.EntryCycleId == cycleId);

        // During results, the placard for a plot you bid on in THIS cycle won't show
        // "your entry" anymore; but the bid was recorded during entry. If we don't
        // find a this-cycle match while in results, also check the immediately prior
        // cycle in case the clock rolled while the record is genuinely older.
        if (keyMatch == null && snap.Phase == LottoPhase.Results)
        {
            keyMatch = bids.Find(b =>
                b.ContentId == contentId &&
                b.TerritoryTypeId == snap.TerritoryTypeId &&
                b.Ward == snap.Ward &&
                b.Plot == snap.Plot &&
                b.EntryCycleId == cycleId - 1);
        }

        var rec = keyMatch;
        var isNew = false;
        if (rec == null)
        {
            // The "For Sale" placard does NOT reveal whether *you* bid on it — it
            // looks identical whether you've entered or are just browsing. So the
            // scrape/hook path must NOT create rows from a placard view; doing so
            // adds every for-sale plot you open. A row is only created when we have
            // positive proof of your entry: a captured entry number, or a win hint.
            // Creation otherwise comes from the chat confirmation or Timers panel.
            if (snap.EntryNumber < 0 && !snap.WonHint)
                return null;

            rec = new BidRecord
            {
                ContentId = contentId,
                EntryCycleId = cycleId,
                EntryDateUtc = now,
            };
            isNew = true;
            bids.Add(rec);
        }

        // --- Identity / location (always refresh) ---
        rec.CharacterName = charName;
        rec.WorldName = worldName;
        rec.Region = region;
        rec.AccountKey = accountKey;
        rec.TerritoryTypeId = snap.TerritoryTypeId;
        rec.District = snap.District;
        rec.Ward = snap.Ward;
        rec.Plot = snap.Plot;
        if (snap.Size != LottoPlotSize.Unknown) rec.Size = snap.Size;
        if (snap.IsFreeCompany) rec.IsFreeCompany = true;

        // --- Lottery state ---
        rec.Phase = snap.Phase;
        if (snap.EntryNumber >= 0) rec.EntryNumber = snap.EntryNumber;   // never overwrite a captured number with -1
        if (snap.WinningNumber >= 0) rec.WinningNumber = snap.WinningNumber;

        // --- Timing ---
        // Prefer the exact results time parsed from the placard's "Accepting entries
        // until ..." line. Only if that's unavailable do we leave it null (we do not
        // fabricate a date from the cycle clock, which can be a day off).
        if (snap.ResultsLocal != null)
        {
            var resUtc = snap.ResultsLocal.Value.ToUniversalTime();
            rec.ResultsAvailableUtc = resUtc;
            rec.ClaimDeadlineUtc = resUtc + LottoCycle.ResultsLength;
        }
        // else: leave whatever a prior precise capture set (may stay null).

        // --- Outcome inference ---
        // Win: explicit hint, or (winning number known AND matches your entry).
        if (rec.Outcome == LottoOutcome.Pending)
        {
            if (snap.WonHint)
            {
                rec.Outcome = LottoOutcome.Won;
                rec.OutcomeRecordedUtc = now;
            }
            else if (rec.WinningNumber >= 0 && rec.EntryNumber >= 0)
            {
                rec.Outcome = rec.WinningNumber == rec.EntryNumber
                    ? LottoOutcome.Won
                    : LottoOutcome.Lost;
                rec.OutcomeRecordedUtc = now;
            }
            else if (rec.ClaimDeadlineUtc != null && now > rec.ClaimDeadlineUtc.Value)
            {
                // Cycle fully elapsed and we never recorded a win → treat as lost.
                rec.Outcome = LottoOutcome.Lost;
                rec.OutcomeRecordedUtc = now;
            }
        }

        rec.LastSeenUtc = now;
        rec.Source = "Live";
        _ = isNew; // (kept for clarity / future telemetry)
        return rec;
    }

    // Build/merge a bid from the chat confirmation emitted at entry time. This is the
    // primary capture path — the message carries plot, ward, district, your lottery
    // number, and the exact results time. Returns the record (caller persists).
    public static BidRecord CaptureFromChat(
        List<BidRecord> bids,
        ChatLotteryParser.Parsed p,
        ushort territoryTypeId,
        ulong contentId,
        string charName,
        string worldName,
        string region,
        string accountKey,
        bool isFreeCompany)
    {
        var now = DateTime.UtcNow;

        // Prefer the precise results time from chat; else derive from the cycle clock.
        DateTime? resultsUtc = p.ResultsLocal?.ToUniversalTime();
        var cycleId = resultsUtc != null
            ? LottoCycle.CycleIdFor(resultsUtc.Value - LottoCycle.EntryLength)  // back out entry start
            : LottoCycle.CycleIdFor(now);

        var rec = bids.Find(b =>
            b.ContentId == contentId &&
            b.TerritoryTypeId == territoryTypeId &&
            b.Ward == (byte)p.Ward &&
            b.Plot == (byte)p.Plot &&
            b.EntryCycleId == cycleId);

        if (rec == null)
        {
            rec = new BidRecord
            {
                ContentId = contentId,
                EntryCycleId = cycleId,
                EntryDateUtc = now,
            };
            bids.Add(rec);
        }

        rec.CharacterName = charName;
        rec.WorldName = worldName;
        rec.Region = region;
        rec.AccountKey = accountKey;
        rec.TerritoryTypeId = territoryTypeId;
        rec.District = string.IsNullOrEmpty(p.District) ? rec.District : p.District;
        rec.Ward = (byte)p.Ward;
        rec.Plot = (byte)p.Plot;
        rec.IsFreeCompany = isFreeCompany;
        rec.Phase = LottoPhase.Entry;

        if (p.LotteryNumber >= 0) rec.EntryNumber = p.LotteryNumber;

        // Timing: use the exact chat datetime when present.
        if (resultsUtc != null)
        {
            rec.ResultsAvailableUtc = resultsUtc;
            rec.ClaimDeadlineUtc = resultsUtc.Value + LottoCycle.ResultsLength;
        }
        else
        {
            rec.ResultsAvailableUtc = LottoCycle.ResultsStart(cycleId);
            rec.ClaimDeadlineUtc = LottoCycle.ClaimDeadline(cycleId);
        }

        rec.LastSeenUtc = now;
        rec.Source = "Live";
        return rec;
    }

    // Build/merge a bid from the "Housing Lottery Status" timers popup. Lets a user
    // backfill a bid placed before installing the plugin, and supplies the FC-vs-
    // personal flag the chat line lacks. Timing is derived from the cycle clock since
    // the panel doesn't state an exact results datetime. Returns the record or null.
    public static BidRecord? CaptureFromStatus(
        List<BidRecord> bids,
        LotteryStatusParser.Parsed s,
        ushort territoryTypeId,
        ulong contentId,
        string charName,
        string worldName,
        string region,
        string accountKey)
    {
        if (!s.HasEntry || contentId == 0) return null;
        if (s.Plot <= 0 || s.Ward <= 0) return null;

        var now = DateTime.UtcNow;
        var cycleId = LottoCycle.CycleIdFor(now);

        var rec = bids.Find(b =>
            b.ContentId == contentId &&
            b.TerritoryTypeId == territoryTypeId &&
            b.Ward == (byte)s.Ward &&
            b.Plot == (byte)s.Plot &&
            b.EntryCycleId == cycleId);

        if (rec == null)
        {
            rec = new BidRecord
            {
                ContentId = contentId,
                EntryCycleId = cycleId,
                EntryDateUtc = now,
            };
            bids.Add(rec);
        }

        rec.CharacterName = charName;
        rec.WorldName = worldName;
        rec.Region = region;
        rec.AccountKey = accountKey;
        rec.TerritoryTypeId = territoryTypeId;
        if (!string.IsNullOrEmpty(s.District)) rec.District = s.District;
        rec.Ward = (byte)s.Ward;
        rec.Plot = (byte)s.Plot;
        rec.IsFreeCompany = s.IsFreeCompany;   // the panel tells us this directly
        rec.Phase = LottoPhase.Entry;

        if (s.LotteryNumber >= 0) rec.EntryNumber = s.LotteryNumber;

        rec.Size = s.Size switch
        {
            LotteryStatusParser.LottoSizeHint.Small => LottoPlotSize.Small,
            LotteryStatusParser.LottoSizeHint.Medium => LottoPlotSize.Medium,
            LotteryStatusParser.LottoSizeHint.Large => LottoPlotSize.Large,
            _ => rec.Size,
        };

        // The status panel does NOT state an exact results datetime. Do not
        // fabricate one from the cycle clock (it can be a day off). Leave the
        // results/claim fields untouched: they stay null unless a precise source
        // (the chat confirmation) fills them in. The UI shows "unknown" for null.
        // (No timing assignment here on purpose.)

        rec.LastSeenUtc = now;
        if (string.IsNullOrEmpty(rec.Source) || rec.Source == "manual") rec.Source = "Live";
        return rec;
    }

    // Build/merge a bid from the placard sale-info hook — the most reliable source.
    // Gives the exact phase-end timestamp (lottery entry deadline = results-open),
    // tenant type (FC vs personal), and phase, with a precise plot location. Only
    // records lottery plots. Returns the record, or null if not a trackable bid.
    public static BidRecord? CaptureFromSaleInfo(
        List<BidRecord> bids,
        Game.PlacardSaleHook.SaleInfo info,
        string district,
        ulong contentId,
        string charName,
        string worldName,
        string region,
        string accountKey)
    {
        if (contentId == 0) return null;
        if (!info.IsLottery) return null;                 // only lottery plots
        if (info.WardId == byte.MaxValue) return null;

        var ward = (byte)(info.WardId + 1);               // hook is 0-based
        var plot = (byte)(info.PlotId + 1);
        if (ward == 0 || plot == 0) return null;

        var now = DateTime.UtcNow;

        // Exact deadline from the game = results-open time (entry closes, results begin).
        DateTime? resultsUtc = info.PhaseEndsAtUtc;

        // Derive the cycle from the precise time when we have it; else from now.
        var cycleId = resultsUtc != null
            ? LottoCycle.CycleIdFor(resultsUtc.Value - LottoCycle.EntryLength)
            : LottoCycle.CycleIdFor(now);

        var rec = bids.Find(b =>
            b.ContentId == contentId &&
            b.TerritoryTypeId == info.TerritoryTypeId &&
            b.Ward == ward &&
            b.Plot == plot &&
            b.EntryCycleId == cycleId);

        // The hook fires for ANY placard you view, not just ones you bid on. Only
        // create a new record if this is an entry you actually placed — we can't tell
        // that from the hook alone, so we only UPDATE existing records here, and let
        // the chat/status paths CREATE them. Exception: during results we still only
        // update. This prevents browsing placards from fabricating bids.
        if (rec == null) return null;

        rec.CharacterName = charName;
        rec.WorldName = worldName;
        rec.Region = region;
        rec.AccountKey = accountKey;
        rec.TerritoryTypeId = info.TerritoryTypeId;
        if (!string.IsNullOrEmpty(district)) rec.District = district;
        rec.Ward = ward;
        rec.Plot = plot;
        rec.IsFreeCompany = info.IsFreeCompany;           // definitive from the game
        rec.Phase = info.InResults ? LottoPhase.Results : LottoPhase.Entry;

        if (resultsUtc != null)
        {
            rec.ResultsAvailableUtc = resultsUtc;
            rec.ClaimDeadlineUtc = resultsUtc.Value + LottoCycle.ResultsLength;
        }

        rec.LastSeenUtc = now;
        return rec;
    }

    // Lazily advance outcomes/timers for stale rows without needing a placard read
    // (e.g. a bid whose claim window simply elapsed). Returns true if anything
    // changed so the caller can persist.
    public static bool TickOutcomes(List<BidRecord> bids)
    {
        var changed = false;
        var now = DateTime.UtcNow;
        foreach (var b in bids)
        {
            if (b.Outcome != LottoOutcome.Pending) continue;
            if (b.ClaimDeadlineUtc != null && now > b.ClaimDeadlineUtc.Value)
            {
                // Only auto-resolve to Lost if we never saw a win signal.
                b.Outcome = LottoOutcome.Lost;
                b.OutcomeRecordedUtc = now;
                changed = true;
            }
        }
        return changed;
    }

    // Build a bid from user-entered fields (backfilling a bid placed before the
    // plugin was installed, or one on a plot you can't currently visit). Timing is
    // derived from the supplied entry date via the global cycle clock. Returns the
    // new record; the caller persists it.
    public static BidRecord CreateManual(
        ulong contentId,
        string charName,
        string worldName,
        string region,
        string accountKey,
        ushort territoryTypeId,
        string district,
        byte ward,
        byte plot,
        LottoPlotSize size,
        bool isFreeCompany,
        DateTime entryDateUtc,
        int entryNumber,
        string notes)
    {
        var cycleId = LottoCycle.CycleIdFor(entryDateUtc);
        var rec = new BidRecord
        {
            ContentId = contentId,
            CharacterName = charName,
            WorldName = worldName,
            Region = region,
            AccountKey = accountKey,
            TerritoryTypeId = territoryTypeId,
            District = district,
            Ward = ward,
            Plot = plot,
            Size = size,
            IsFreeCompany = isFreeCompany,
            EntryDateUtc = entryDateUtc,
            EntryCycleId = cycleId,
            EntryNumber = entryNumber < 0 ? -1 : entryNumber,
            Notes = notes,
            Source = "manual",
            Phase = LottoCycle.PhaseFor(DateTime.UtcNow),
            ResultsAvailableUtc = LottoCycle.ResultsStart(cycleId),
            ClaimDeadlineUtc = LottoCycle.ClaimDeadline(cycleId),
            LastSeenUtc = DateTime.UtcNow,
        };

        // If the cycle has fully elapsed and no win was recorded, mark as lost.
        if (rec.ClaimDeadlineUtc != null && DateTime.UtcNow > rec.ClaimDeadlineUtc.Value)
        {
            rec.Outcome = LottoOutcome.Lost;
            rec.OutcomeRecordedUtc = DateTime.UtcNow;
        }

        return rec;
    }
}
