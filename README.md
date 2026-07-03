# Housing Lotto Tracker

A Dalamud plugin that passively tracks your FFXIV housing lottery bids across all your characters, and alerts you when plots you're watching open up.

Open with `/hlt` (alias `/lotto`).

## Install

In Dalamud, go to **Settings → Experimental → Custom Plugin Repositories**, add the URL below, save, then install **Housing Lotto Tracker** from the plugin installer.

```
https://raw.githubusercontent.com/dexcss/DalamudPlugins/main/repo.json
```

## What it tracks

One row per bid, showing: character (with world), region, district, ward, plot, plot size, FC vs Personal, your bid number, the entry date, the results date with a live countdown, and the outcome (Pending / Won / Lost / Claimed). Columns can be shown, hidden, and reordered in Settings.

## How bids are recorded

Capture is passive — you don't type anything for a normal bid.

- **When you place a bid**, the in-game confirmation ("You have submitted a lottery entry for…") is read automatically, filling in the character, world, location, your bid number, plot size, and exact results date.
- **For bids placed before installing**, open **Duty → Timers → Estate → Housing Lottery Status** to record the entry, then open that plot's placard to lock in the precise results date. Opening a placard never creates a row on its own — it only enriches a bid already recorded, so browsing for-sale plots won't clutter the list.
- **Add bid** lets you enter one fully by hand, for plots you can't currently visit.

Wins are detected automatically when the results placard congratulates you.

## Travel & login buttons

With **Lifestream** and/or **AutoRetainer** installed, each row gets two buttons:

- **Login** — logs into that bid's character (via AutoRetainer relog).
- **Travel** — Lifestreams to that bid's plot.

Both are on by default and toggleable in Settings → Columns. Each is hidden per-row when the matching plugin isn't installed.

## Open-plot alerts

Create watch rules in **Settings → Alerts** to be notified when a matching plot opens, using crowd-sourced data from **PaissaDB** (the same source as the PaissaHouse plugin).

Rules can be broad or specific — *"any Shirogane Plot 60, any server"* or *"Shirogane Plot 60, Ward 26, on Ultros."* You can multi-select plots, wards, sizes, worlds, data centers, and regions; a blank filter means "any." Plot and ward fields accept ranges, so `31-60` covers a whole subdivision.

When a match appears a popup lists it (with a Travel button if Lifestream is installed). Hitting **Seen** acknowledges that plot so it won't alert again until it fills and re-opens. You can have alerts pop only at login or whenever a new match appears, and set the check interval. Coverage depends on other players reporting plots, exactly like PaissaHouse.

## Notes & limitations

- The game doesn't store your bid number after entry — it's captured live from the confirmation. If a bid is added another way, the number is an editable field.
- Results dates come from the game's own data (the entry confirmation or the placard deadline). A bid recorded only from the Timers panel shows "date unknown" until you open its placard once.
- Shared storage is on by default: all clients on the same Windows user share one dataset regardless of `--roamingPath`, with multi-client-safe merging. Turn it off in Settings for a per-install config.

## Building

CI builds and releases on tag push (e.g. `v1.0.0.0`). To bump the version, update all three: `HousingLottoTracker.csproj`, `HousingLottoTracker.json`, and `repo.json`.
