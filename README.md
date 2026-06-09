# BetterTips

A Dalamud plugin that trims the clutter out of FFXIV **item tooltips**.

Item tooltips carry a lot of lines you may never read — durability and spiritbond bars, the
extractable / projectable / desynthesizable flags, repair and melding details, lore flavor, and the
keybind hints at the bottom. BetterTips lets you hide the sections you don't care about so the tooltip
shows only what matters to you.

## How it works

BetterTips hooks the game's item-tooltip generation (the same entry point the SimpleTweaks tooltip
tweaks use) and blanks the lines for any section you've turned off, so the tooltip collapses to just the
parts you keep. The game's own layout is preserved — nothing is reflowed or restyled.

## Usage

- `/bettertips` (or `/btips`) — open the settings window.
- In settings, tick the sections you want to **see**; unticked sections are hidden.
- `/bettertips dump` — developer aid: hover an item afterward to log every tooltip line and its field
  index to the Dalamud log (`/xllog`). Used to map additional tooltip lines.

By default, **Extractable / Projectable / Desynthesizable** and **Durability / Spiritbond / repair** are
hidden; everything else is shown.

## Building

```sh
dotnet build BetterTips.csproj -c Debug
```

Requires the Dalamud reference assemblies. The project looks for them in the sibling `../dalamud/`
directory by default, or set the `DALAMUD_HOME` environment variable to your Dalamud `dev` folder.
