# Vendor Inventory Manager 1.4.3

## Changes

- Fixed overlapping random pools counting the same existing item, fixed-stock
  item, or pending selection toward multiple pool targets.
- Fixed stock is reserved first, and each remaining existing stock item can
  satisfy only one random pool. Compatible allocations can move so a broad
  pool does not unnecessarily consume the only item eligible for a narrower
  quality or source filter.
- Reapplying unchanged rules reuses existing allocations instead of repeatedly
  adding another complete batch. Five successful quantity-one pools require
  five separate allocations in addition to fixed stock, subject to available
  candidates and duplicate restrictions.
- Added per-pool diagnostics for targets, existing allocations, queued items,
  skipped chance rolls, empty filters, and duplicate-name shortages.
- Updated the editor help and README, and included standalone allocation
  regression tests with the source.

## Installation

Requires Silverpine 1.7.3, BepInEx 5, and Modding Tools. Modding Tools supplies
Newtonsoft.Json; do not install a separate copy with this plugin. Custom Item
Loader remains optional.

Close Silverpine and extract `VendorInventoryManager-1.4.3.zip` into
`BepInEx/plugins/VendorInventoryManager/`, replacing the old plugin files.
Alternatively, replace the DLL in that folder with the standalone download.
Keep only one installed copy of the DLL and preserve your existing vendor
configuration and saves.

Restart the game, load your save, and use `Save + Apply` in Vendor Inventory
Manager to fill missing stock immediately. Waiting for midnight is not required.

## Validation

The Release build passed with zero warnings and errors. All 11 named allocation
regression cases and 512 exhaustive overlap cases passed. Live in-game
confirmation was not performed for this release.

The ZIP includes only the plugin DLL, README, and these notes. No game assemblies,
debug symbols, credentials, saves, or player configuration are included.

Created by Saelac and ChatGPT.
