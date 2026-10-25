# ItemLookMerger

A Synthesis patcher for Skyrim Special Edition that combines the **look** of items from look mods with the **stats** from
whichever plugin wins them otherwise. Look mods no longer have to win the record, and no SkyPatcher is needed.

## How it works

**Items covered:** armor, weapons, ammunition, misc items, books, ingredients, potions and food, soul gems, keys.

**Look** (taken from the look plugins): world and inventory models with their alternate textures, icons, armor addons, first-person
and scope models, book inventory art, and the name when **Forward names** is on. The armor addons, texture sets, statics and arrow
projectiles these use are look records: a look plugin's changes to their meshes and textures are forwarded too, while races, slots,
speed, sounds and the like stay as they were.

**Stats** (everything else) come from the winning plugin among the non-look plugins of your load order, or from an earlier patcher
in the same Synthesis group. When no other plugin changes an item, the look plugin's whole item is used, stats included.

When several look plugins change the same item, the one **later in the load order** wins.

Items whose look already matches are left alone. The listed plugins must be active in the load order when Synthesis runs.

### Content merging on (default)

The patch is self-contained: it needs none of the look plugins as a master, so **you can disable the look mods after the run** and
keep their looks (enable them again for the next run).

- The armor addons, texture sets, statics and projectiles of the look plugins that the forwarded looks use are copied into the patch.
- Only these visual records are carried. A field outside the look that points at a look plugin's own record (a keyword, an
  enchantment, a script, ...) keeps the stats side's value instead, and the log names the item and field.
- The look mods' meshes and textures are copied into the forward folder (see Assets).

### Content merging off

The patch keeps the look plugins as masters, so they stay active. No files are copied and the forward folder is left as it is.

## Assets

With merging on, each successful run replaces the content of the forward folder (an MO2 mod, `OUTPUT ItemLookMerger` by default)
with the meshes and textures the forwarded looks need, including the textures inside those meshes.

- Files are copied only from the look mods: their loose files first, then their BSAs.
- Base-game files are skipped. Files that only another mod provides are not copied, and the log says where they come from.
- When two look mods have the same file, the one with the higher priority in MO2's left panel provides it.
- The new content is written completely before it replaces the old, so a failed run keeps the previous output. The patcher only
  manages a folder it created (marked by a hidden `.itemlookmerger` file).

Enable the forward folder in MO2 and give it a high priority.

## CREDITS.md

One row per changed item: its EditorID, name and FormKey, the look plugins it takes its look from and the plugin its stats come
from, each with its MO2 mod. Below that, each mod's Nexus link, version and download file as recorded in MO2. It is written to the
forward folder with merging on, and next to the Synthesis output with merging off.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| **Plugins to forward** | empty | The look plugins. Each must be active when the patcher runs. |
| **Forward names** | off | Also take item names from the look plugins. |
| **Verbose logging** | off | Log every item, field and asset decision. |
| **Content merging → Allow merging** | on | Self-contained patch and asset copying, as described above. |
| **Content merging → Forward folder name** | `OUTPUT ItemLookMerger` | The MO2 mod folder (directly in the mods folder) that receives the assets and CREDITS.md. Its content is replaced on every run, so use it for nothing else. |
