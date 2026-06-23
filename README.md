# Housing Lotto Tracker

A Dalamud plugin (API15) that passively tracks your FFXIV housing lottery bids across all your characters.

Open with `/hlt` (alias `/lotto`).

## What it tracks (one row per bid)

- Character (with world) and region (JP/NA/EU/OCE/CN/KR/CLD/TCN)
- District, ward, plot, and plot size
- FC vs Personal bid
- Entrant count (as read from the placard)
- Your entry number (auto-grabbed if the game shows it; editable otherwise)
- Entry date and results-available date, with a live countdown
- Outcome: Pending / Won / Lost / Claimed

## How capture works

Capture is **passive**. There are two ways a bid gets recorded:

1. **Automatically when you place a bid.** The in-game confirmation ("You have submitted a lottery entry for…") is read the moment you enter, recording the character, world, district, ward, plot, your lottery number, plot size, and the exact results date.
2. **By checking the game's own records** (for bids placed before installing the plugin). Open **Duty → Timers → Estate → Housing Lottery Status** to record the entry, then walk up to that plot and open its placard to fill in the precise results date. Opening the placard alone never creates a row — it only enriches a bid the plugin already knows about, so browsing other for-sale plots won't clutter the list.

You can also enter a bid **fully by hand** with the **Add bid** button, for plots you can't currently visit or anything you'd rather type in yourself.

Wins are auto-detected when the results placard's confirmation dialog congratulates you.

## Honest limitations

- **The game does not persist your personal ticket number.** It's shown briefly at
  bid time. The plugin grabs it if it's visible when you read the placard;
  otherwise the field is editable so you can type it in.
- **Cycle timing is day-accurate, not second-accurate.** Results dates are derived
  from the global 9-day lottery loop (5-day entry + 4-day results) anchored to a
  known cycle. Regional reset *hours* aren't modelled, which only nudges the day
  boundary. If precise per-region reset hours are needed later, they can be layered
  onto `LottoCycle`.
- Entrant counts reflect the value at your last placard read, not live updates.

## Storage

Shared storage is on by default: all clients on the same Windows user share one
`shared.json`, regardless of `--roamingPath`, with multi-client-safe merging (same
model as FC Tracker). Turn it off in Settings to use the per-install config instead.

## Building

CI builds on tag push (`v1.0.0.0`). To bump the version, update **all three**:
`HousingLottoTracker.csproj`, `HousingLottoTracker.json`, and `repo.json`.
