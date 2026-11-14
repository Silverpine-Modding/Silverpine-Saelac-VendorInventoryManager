# Vendor Inventory Manager

Vendor Inventory Manager is a BepInEx plugin for Silverpine 1.7.3. It adds a
Modding Tools editor on both the main menu and in-game Mods tab for assigning
additional vendor stock to:

- Base-game vendors
- Enabled custom characters whose `possibleRoutines` contain
  `RoutineArgument_BeVendor`

The plugin can add fixed minimum stock, exclude exact items from automatic
stock, and generate random stock from configurable item filters. Inventory
edits are separated into named packs that can be enabled or disabled
independently. It does not replace or rewrite the base game's loot tables.

## Installation

The installed folder is:

```text
BepInEx/plugins/VendorInventoryManager/
```

It contains:

```text
VendorInventoryManager.dll
README.md
```

The plugin has a hard dependency on the shared `ModdingTools` plugin and
registers its editor through `ModdingToolsMenu.RegisterSession` and
`InventoryModTools.RegisterSession`. It does not create a separate menu
framework or modify Modding Tools. Modding Tools also supplies Newtonsoft.Json
13.0.3; do not add a private `Newtonsoft.Json.dll` to this folder.

Custom Item Loader is an optional soft dependency. When it is installed,
Vendor Inventory Manager loads after it so registered custom items are
available in the item tree.

## Using the editor

1. Launch Silverpine.
2. Open either:
   - `Modding Tools` from the main menu, then `Vendor Inventory Manager`; or
   - the in-game inventory/pause menu, select `Mods`, then
     `Vendor Inventory Manager`.
3. The `M` shortcut can also open the pause menu directly on its `Mods` tab.
4. Use the `Stock pack` bar to select an existing pack or choose `New Pack`.
5. Rename the pack and set it to `Enabled` or `Disabled` as needed.
6. Select a base or custom vendor in the left panel.
7. Search or expand the source/category branches in the `Item tree`. Custom
   items are grouped by their Custom Item Loader pack ID before category.
8. Choose `Add Fixed` beside an item to guarantee a minimum quantity, or
   `Exclude Auto` to keep that exact item out of automatic stock.
9. Expand `Random stock pools` to add category/name/source/value/pickupability
   filters and set their roll chance and quantity range. Each pool has its own
   arrow for minimizing its detailed options while retaining a compact summary.
10. Choose `Save + Apply`. Fixed stock and enabled random pools apply to live
    vendors immediately. Exclusions apply on the next automatic stock cycle.

The manager keeps its daily-restock patch active after Silverpine destroys the
initial BepInEx bootstrap host. This is expected game behavior; it does not
unload the mod. Fixed entries and enabled random pools are reapplied after each
vendor `OnNewDay` restock.

Pack deletion requires two clicks: `Delete Pack`, followed by
`Confirm Delete`. At least one pack is always retained. A disabled pack remains
fully editable but contributes no stock when rules are applied.

The left panel is polled every two seconds while the editor is open. The
`Poll Vendors` button performs an immediate refresh.

The item tree has two top-level branches:

```text
Base game
  Category
    Item

Custom items
  Pack ID
    Category
      Item
```

Search matches item names, descriptions, categories, pack IDs, and the source
label.
Search results automatically open their matching tree branches. Item actions
apply to the currently selected vendor and stock pack.

## Vendor discovery

The following Silverpine 1.7.3 base vendors are always listed:

- Aldric
- Gareth
- Hayden
- Rosalyn

The plugin also discovers live NPC objects carrying both `NeuralNPC` and
`Vendor`.

For custom characters, it polls
`CustomContentDefinition_NPC.loaded`. A definition qualifies when:

- It is enabled.
- It is a custom character rather than a disabled definition.
- At least one entry in `possibleRoutines` contains an actual
  `RoutineArgument_BeVendor` object.

Matching visible text such as "working in a shop" is not sufficient. The
routine must carry the vendor argument used by Silverpine.

Configured vendors remain visible as unavailable entries if their custom
definition is temporarily missing or disabled. This allows their stock rules
to be repaired or removed without recreating the character.

## Stock behavior

Each configured quantity is a minimum amount of vendor-tagged stock.

For fixed entries whose template implements `IQualityItemComponent`, the
editor exposes `Template`, `Normal`, `Quality`, `Superior`, `Exceptional`,
`Pristine`, `Blessed`, and `Legendary`. `Template` preserves the registered
item's current quality. A selected level is applied only to the vendor copies
created by that rule, and the minimum quantity is counted for that exact
quality rather than being satisfied by lower- or higher-quality copies.

Only enabled packs participate. If several enabled packs configure the same
item for the same vendor, the highest minimum wins. For example, quantities of
two and five produce an effective minimum of five, not seven. This makes packs
safe to layer without multiplying duplicate stock.

Vendor Inventory Manager applies it:

- After a save finishes loading
- After custom NPC registration finishes
- After `Vendor.OnNewDay` completes its normal daily restock
- Immediately when `Save + Apply` is chosen and the vendor is live

For example, if Aldric's normal loot table already supplied one Iron Sword and
the configured quantity is three, the plugin adds two. Every injected item is
a fresh deep clone and receives an `ItemComponent_VendorItem` belonging to the
selected NPC, so Silverpine includes it in that NPC's sale inventory and LLM
vendor-contents description.

The temporary insertion path permits vendor inventories to exceed their
ordinary bulk and slot limits, then restores the original inventory settings.
This prevents a heavy configured item from silently disappearing while
leaving the vendor's saved capacity rules unchanged.

Removing an entry or disabling its pack stops future top-ups. It does not
forcibly delete copies already present in a live or saved vendor inventory.
Silverpine's next native daily restock clears and rebuilds that stock normally.

### Automatic-stock exclusions

An exclusion is an exact item display name scoped to one vendor and one pack.
Enabled packs are combined, so an item excluded by any enabled pack is withheld
from that vendor's native predicate-based loot selection and removed if the
native table added it directly by name. Exclusions also remove the item from
Vendor Inventory Manager random-pool candidates.

Fixed entries are explicit overrides: a fixed entry can still add an item that
is also excluded from automatic stock. This makes it possible to prevent a
native random quantity while guaranteeing a controlled fixed quantity.

During vendor restock, the predicate boundary is null-safe. A null library
entry or an item that throws `NullReferenceException` while Silverpine tests
it is skipped, and the BepInEx warning identifies the vendor and item when one
is available. This prevents one malformed mod item from aborting the remaining
predicate-based stock generation.

### Random stock pools

Each enabled pool rolls independently after the vendor's normal daily restock
and on `Save + Apply`. Custom vendors also receive a roll when their vendor NPC
is first registered. A pool chooses a target between `minQuantity` and
`maxQuantity`.

Each existing vendor-tagged item can count toward only one target. Fixed stock
is reserved first, then the remaining items are assigned to random pools.
Pending fixed stock and new selections from other pools cannot fill a pool's
target. Overlapping filters therefore keep separate quantities: five pools
with 100% chance and quantity 1 need five items in addition to fixed stock,
provided their filters and duplicate restrictions permit it. Reapplying the
same rules reuses those allocations rather than adding five more items every
time. Existing native vendor stock can fill random targets when it is not
needed by fixed stock.

Each roll logs its target, existing allocation, and queued quantity. Failed
chance rolls, empty candidate filters, and shortages caused by duplicate-name
restrictions are reported separately. Clone or insertion failures are also
logged with the affected item name.

Available filters are:

- `source`: selected from an in-editor dropdown containing `Any`, `BaseGame`,
  `Custom`, and every discovered Custom Item Loader pack. JSON uses
  `Pack:<packId>` for a specific pack. Custom Item Loader items and their pack
  IDs are recognized from the stable `custom:<packId>:<itemId>` sprite key.
- `category`: `Any` or any Silverpine `ItemCategory` value.
- `pickupable`: `Any`, `Pickupable`, or `VendorCarryable`.
- `quality`: `Template` or a Silverpine quality from `Normal` through
  `Legendary`. Selecting a level limits the pool to quality-capable items,
  applies that quality to generated vendor copies, and counts only matching
  quality toward the pool target. `Template` preserves existing behavior.
- `nameContains` and `nameExcludes`: optional case-insensitive substrings.
- `minValue` and `maxValue`: optional inclusive gold-value bounds.
- `chancePercent`: chance from 0 through 100 for the pool to run.
- `allowDuplicates`: allows repeated selection of the same item name when
  filling the target.

Random pools never select names excluded from automatic stock. A pool with no
matching registered items is skipped and reported in the BepInEx log.

The NPC's existing vendor routine still controls when trading is active.
Adding stock does not make a non-vendor NPC trade.

## Configuration

Rules are stored in:

```text
BepInEx/config/VendorInventoryManager/vendor-stock.json
```

Example:

```json
{
  "version": 4,
  "packs": [
    {
      "id": "default",
      "name": "Default",
      "enabled": true,
      "vendors": [
        {
          "vendorId": "base:Aldric",
          "items": [
            {
              "itemName": "Iron Sword",
              "quantity": 2,
              "quality": "Superior"
            }
          ],
          "automaticStockExclusions": [
            "Simple Bed"
          ],
          "randomPools": [
            {
              "id": "random-1",
              "enabled": true,
              "source": "Custom",
              "category": "Food",
              "pickupable": "Any",
              "quality": "Template",
              "nameContains": "",
              "nameExcludes": "Spoiled",
              "minValue": 1,
              "maxValue": 100,
              "minQuantity": 2,
              "maxQuantity": 5,
              "chancePercent": 75.0,
              "allowDuplicates": false
            }
          ]
        }
      ]
    }
  ]
}
```

Version-1 files containing a top-level `vendors` array are migrated in memory
to an enabled pack named `Default`. Existing version-2 pack files load with
empty exclusions and random pools. Version-3 fixed entries and pools load with
`Template` quality. The next editor save writes version 4. Pack IDs are stable
internal identifiers; pack names can be changed freely.

Vendor IDs use:

```text
base:<NPCName enum value>
custom:<customContentName>
```

Item names are used because Silverpine's `ItemLibrary` requires unique display
names and returns deep clones by name. Missing items are skipped and reported
once in the BepInEx log.

The editor writes the JSON atomically through a temporary file. `Reload`
discards unsaved in-memory changes and rereads the file.

## Compatibility and safety

- Modding Tools remains the sole main-menu injection framework.
- Native loot-table definitions are not rewritten. Automatic-stock exclusions
  only filter or remove their configured exact names during the selected
  vendor's restock.
- Custom Item Loader items are discovered from `ItemLibrary.Items`.
- Item templates are never inserted directly into an inventory; each stock
  copy is deep-cloned.
- Quality overrides are applied only after cloning and never mutate registered
  base-game or custom-item templates.
- Duplicate stock is prevented by counting existing vendor-tagged items before
  topping up.
- Custom vendors are addressed by `customContentName`, not their transient
  `CustomNPCSlot`, so rules survive slot reassignment between saves.

## Troubleshooting

### A custom character does not appear

Confirm that its NPC definition is enabled and that at least one possible
routine contains `RoutineArgument_BeVendor`. Choose `Poll Vendors` after
editing the character definition.

### A configured custom item is missing

Confirm that Custom Item Loader registered the item successfully and that its
display name still matches `itemName`. Restart Silverpine after adding or
renaming custom item packs.

### The NPC has stock but will not trade

Stock and vendor activity are separate. The NPC must currently be executing a
routine whose `RoutineArgument_BeVendor` has activated its `Vendor` component.

### An item remains after its rule was removed

Rule removal is non-destructive. The existing copy remains until purchased or
until the vendor's next native daily restock.

## Credits

Created by **Saelac and ChatGPT**.
