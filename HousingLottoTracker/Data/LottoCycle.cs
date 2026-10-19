using System;

namespace HousingLottoTracker.Data;

// The FFXIV housing lottery runs a fixed, continuous 9-day loop: a 5-day entry
// period followed by a 4-day results period. The loop is global (all worlds shift
// at their regional reset). We anchor to a known entry-period start and compute
// the cycle index for any given instant so that two bids placed on different days
// of the same entry window collapse into the same logical cycle.
//
// IMPORTANT: resets happen at a regional time-of-day (e.g. 08:00 PT for NA). We
// approximate by anchoring to a known UTC instant of an entry-period start and
// stepping in 9-day blocks. This is accurate to the day, which is all the tracker
// needs (results "available" dates are day-granular for the user). If a precise
// per-region reset hour is ever needed it can be layered on later.
public static class LottoCycle
{
    public static readonly TimeSpan EntryLength = TimeSpan.FromDays(5);
    public static readonly TimeSpan ResultsLength = TimeSpan.FromDays(4);
    public static readonly TimeSpan CycleLength = TimeSpan.FromDays(9);

    // A known entry-period start. 2026-06-19 15:00 UTC is a verified anchor: the
    // published October 2026 schedule (Polygon) has entry periods beginning
    // Sep 26, Oct 5, and Oct 14, 2026 — all exactly 9*k days from this instant —
    // and periods flip at the daily reset (15:00 UTC / 8:00 a.m. PT).
    // (The original 06-18 anchor was one day early and was corrected in 1.1.0.0;
    // stored cycle ids are migrated from EntryDateUtc on load.)
    public static readonly DateTime Anchor = new DateTime(2026, 6, 19, 15, 0, 0, DateTimeKind.Utc);

    // Cycle id = number of whole 9-day blocks between the anchor and the instant.
    public static long CycleIdFor(DateTime utc)
    {
        var delta = utc - Anchor;
        // Floor division that works for negative spans too.
        var days = delta.TotalDays / CycleLength.TotalDays;
        return (long)Math.Floor(days);
    }

    // Start (entry-period open) of a given cycle id.
    public static DateTime CycleStart(long cycleId) => Anchor + TimeSpan.FromDays(cycleId * 9);

    // Results open 5 days after the entry period starts.
    public static DateTime ResultsStart(long cycleId) => CycleStart(cycleId) + EntryLength;

    // Claim/refund window closes 4 days after results open (= end of the cycle).
    public static DateTime ClaimDeadline(long cycleId) => ResultsStart(cycleId) + ResultsLength;

    // Convenience: classify where 'utc' sits within its own cycle.
    public static LottoPhase PhaseFor(DateTime utc)
    {
        var id = CycleIdFor(utc);
        var into = utc - CycleStart(id);
        return into < EntryLength ? LottoPhase.Entry : LottoPhase.Results;
    }
}
