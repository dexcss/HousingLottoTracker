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

Capture is **passive**. When you open a housing placard (the `HousingSignBoard`
addon) during an entry or results period, the plugin reads the plot location from
`HousingManager` and scrapes the placard's text for the entrant count, your entry
number (when shown), the winning number (during results), and phase. A bid is only
*created* when you're looking at a placard during the entry period, or when the
placard explicitly shows your entry number — so browsing other plots won't create
phantom rows.

Wins are auto-detected the same way FC Tracker detects house wins: the results
placard's confirmation dialog (`SelectYesno`) is checked for congratulatory
wording, and the matching bid is flipped to **Won**.

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
