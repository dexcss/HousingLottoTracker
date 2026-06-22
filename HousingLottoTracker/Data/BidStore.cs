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
            // Only create a NEW bid during the entry phase — seeing a placard during
            // results for a plot you never bid on shouldn't fabricate a row.
            if (snap.Phase != LottoPhase.Entry)
            {
                // Exception: the placard explicitly shows your entry number → you did
                // bid; record it even in results.
                if (snap.EntryNumber < 0 && !snap.WonHint)
                    return null;
            }

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

        // --- Timing (derive from the cycle the bid belongs to) ---
        var bidCycle = rec.EntryCycleId;
        rec.ResultsAvailableUtc = LottoCycle.ResultsStart(bidCycle);
        rec.ClaimDeadlineUtc = LottoCycle.ClaimDeadline(bidCycle);

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
