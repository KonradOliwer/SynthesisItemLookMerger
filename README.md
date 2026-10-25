# ItemLookMerger

A Synthesis patcher for Skyrim Special Edition that combines the **look** of items from look mods with the **stats** from
whichever plugin wins them otherwise. Look mods no longer have to win the record, and no SkyPatcher is needed.

## How it works

**Items covered:** armor, weapons, ammunition, misc items, books, ingredients, potions and food, soul gems, keys.

**Look** (taken from the look plugins): world and inventory models with their alternate textures, icons, armor addons, first-person
and scope models, book inventory art, and the name when **Forward names** is on.

**Forms the look depends on:** for every item a look plugin changes, the patcher follows the links in the look fields, and from
there the links in the visual fields of each form it reaches. It only goes on through forms a look plugin contains (adds or
overrides); a form no look plugin touches ends the walk there. Where several look plugins contain a form, the one later in the load
order provides it. Each form reached becomes a **new record in the patch**, and the item (and the copies) point at the new records:

- A form the look plugin overrides (a vanilla armor addon, texture set, ...): the new record is the winning plugin's version with the
  look plugin's **visual fields**; races, slots, speed, sounds and everything else stay the winner's. Its EditorID gets the suffix
  `_ILM`. The original record is not changed, so other items that use it keep their look. A form whose visual fields already match
  needs no copy, unless a form it links to has one.
- A form the look plugin adds: the new record is the look plugin's version, also with the `_ILM` suffix.
- One new record per form, however many items use it. Two forms with the same EditorID get distinct names (the plugin and FormID
  are added to the second).

**Lists** (an armor's addons, an impact data set's impacts, a form list's entries, a debris' models) are taken from the look plugin
as they are, not merged with what the winning plugin has in them. An entry that points at a form a look plugin contains points at
that form's new record; an entry that points at any other form keeps pointing at it, so in game it is the version your load order
wins.

The weapon and arrow records an item keeps from its stats side (an arrow's projectile, a weapon's impact data) are followed the same
way. An item skipped for a type discrepancy (below) brings nothing along.

| Record | Visual fields |
|---|---|
| Armor addon | world and first-person models, weight slider on or off, skin texture, texture swap lists, art object |
| Texture set | all texture paths, decal data, flags |
| Static | object bounds, model, material and its angle, flags, LOD |
| Moveable static | model |
| Projectile | model, muzzle flash model, light, muzzle flash light, decal (not the explosion) |
| Light | model, radius, color, flags except can-be-carried, falloff, FOV, near clip, flicker, fade, lens flare (not the time) |
| Impact | model, decal, texture sets, decal duration, orientation, angle threshold and placement radius |
| Impact data set | its impacts |
| Art object | model, type |
| Effect shader, material object | everything except sounds |
| Debris | its models |
| Form list | its entries (the texture swap lists of armor addons) |

**Stats** (everything else) come from the winning plugin among the non-look plugins of your load order, or from an earlier patcher
in the same Synthesis group. When no other plugin changes an item, the look plugin's whole item is used, stats included, and the
item's origin master is credited as its stats side.

When several look plugins change the same item, the one **later in the load order** wins, whatever the order of the list or MO2's
priorities.

Items whose look already matches are left alone, and so are other item types' type-like fields (potion kind, book type, soul gem
size): they stay the stats side's. The listed plugins must be active, and above the Synthesis patch, when Synthesis runs.

### Type discrepancies

A look plugin can give an item another type than the one your modlist's winning plugin gives it. It is checked when the look's
model, icon or armor addon differs from the stats side's, so not for a changed name alone, and not when the look plugin's whole item
is used (it is consistent in itself).

| | Narrow (the setting decides) | Wide (always skipped) |
|---|---|---|
| Weapon | another animation type inside the melee family: one-handed sword, dagger, axe or mace, two-handed sword or axe, hand to hand | between melee, bow, crossbow and staff |
| Armor | another armor type (light, heavy, clothing) within one slot class | between the slot classes below |
| Ammunition | | arrow to bolt, or the reverse |
| Other | | a record that your load order defines as another kind of record (a weapon overridden as armor) |

Armor slot classes come from the biped slots: **shield** when the shield slot is set; **jewelry** when it has an amulet, ring, circlet
or ears slot and no head, hair, body, hands, forearms, feet, calves or long hair slot; **body armor** otherwise. A weapon without
weapon data or an armor without a body template has no type to compare.

A wide change is not merged: the item is left untouched, and the log names it at error level with both types and both plugins. The setting only decides narrow changes:

- `TakeVisualModWeaponType` (default): a weapon takes the type-related stats of a base item (below); an armor keeps your modlist's type
  and the look is forwarded. The log counts such armors in one line, and lists them with Verbose logging.
- `TakeModlistItemType`: the stats side keeps its type and the look is forwarded.
- `SkipMerging`: the item is left untouched and the log names it with both types and both plugins.

#### Base item

The base item is a generic weapon of the look's type and of the stats side's material, so the plain greatsword of the same material
for a one-handed sword that looks like a greatsword.

1. The look record's **template** is used when it is such a weapon itself.
2. Otherwise the best weapon that passes the same rules. It has the look's animation type and the same weapon-type keywords
   (`WeapType...`) as the look record, and the same material keywords (`WeapMaterial...`, `DLC1WeapMaterial...`,
   `DLC2WeaponMaterial...`) as the stats side, or as the look record when the stats side has none. It is playable, not enchanted,
   not bound, player-only, undroppable or reserved for non-combat use, without a template, and has weapon data and damage and weight.
   It has to have a weapon-type and a material keyword, and a look plugin must not have introduced it or retype it.
3. Among several: the vanilla masters first (Skyrim, Update, Dawnguard, HearthFires, Dragonborn), then weapons without scripts,
   then the plugin earlier in the load order, then the lowest FormID.

A base item's stats are those its own stats side has (an earlier patcher's version before this run, else the winning non-look
plugin), so they do not depend on the order items are processed. When a look plugin is the only plugin overriding the base item,
its record supplies the base's stats and is credited. With no base item (no weapon-type or material keyword to match, or no such
weapon), the item is left untouched and the log warns.

**Taken from the base item:** the weapon data (animation type, speed, reach, sight FOV, VATS chance, attack animation, projectiles,
embedded actor value, range, on-hit, attack multiplier, rumble, skill, stagger, type flags), damage and weight, critical data (none
when the base has none), equipment type, block and bash and alternate block impact data, the pickup, putdown, attack, idle, equip
and unequip sounds, detection sound level, and the weapon-type and material keywords.

**Kept from the stats side:** name, value, enchantment, scripts, description, destructible, template, object bounds, resist, the
item flags (ignores normal weapon resistance, can't drop, non-playable, player only, minor crime, non-hostile, bound weapon) and all
other keywords. The look fields come from the look record.

### Content merging on (default)

The patch is self-contained: it needs none of the look plugins as a master, so **you can disable the look mods after the run** and
keep their looks (enable them again for the next run).

- The forms the forwarded looks depend on are in the patch as new records (see above), including those the look plugins added.
- Only these visual records are carried. A field outside the look that points at a look plugin's own record (a keyword, an
  enchantment, a script, ...) keeps the stats side's value instead, and the log names the item and field.
- The look mods' meshes and textures are copied into the forward folder (see Assets).

### Content merging off

The patch keeps the look plugins as masters, so they stay active. The forms the looks depend on are new records in the patch the same
way, and they keep their other links, also those into the look plugins. No files are copied and the forward folder is left as it is.

## Assets

With merging on, each successful run replaces the content of the forward folder (an MO2 mod, `OUTPUT ItemLookMerger` by default)
with the meshes and textures the forwarded looks need, including the textures inside those meshes.

- Assets are copied for every listed look plugin, from its MO2 mod: the enabled mod MO2 serves the plugin file from. Never from the
  stats side or any other mod.
- Files are copied only from the look mods: their loose files first, then their BSAs. The other weight's mesh of an armor addon is
  copied too.
- Base-game files are skipped. Files that only another mod provides are not copied, and the log says where they come from.
- When two look mods have the same file, the one with the higher priority in MO2's left panel provides it.
- The new content is written completely before it replaces the old, so a failed run keeps the previous output. The patcher only
  manages a folder it created (marked by a hidden `.itemlookmerger` file).

Enable the forward folder in MO2 and give it a high priority.

## CREDITS.md

One row per changed item: its EditorID, name and FormKey, the look plugins it takes its look from (the item's own and those of the
forms its look depends on) and the plugin its stats come from, each with its MO2 mod. A weapon that took its type from a base item names that
base item and the plugin its stats came from. Below that, each mod's Nexus link, version and download file as recorded in MO2;
base-game plugins and mods without a `meta.ini` are listed by plugin name only. It is written to the forward folder with merging
on, and next to the Synthesis output with merging off.

## MO2 instance and profile

MO2 does not tell the patcher which instance and profile it runs in, so the patcher takes the only one whose active plugins are
exactly those Synthesis listed (also the plugins below the patch), in the same order, each coming from an enabled mod or the game's
Data folder. Copies of one profile with an identical mod list count as one.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| **Plugins to forward** | empty | The look plugins. Each must be active, above the Synthesis patch, when the patcher runs. |
| **Forward names** | off | Also take item names from the look plugins. |
| **In case of type discrepancy** | `TakeVisualModWeaponType` | What to do when the look's weapon or armor type differs narrowly from the stats (see Type discrepancies): `TakeVisualModWeaponType` (weapons take the type stats of a generic base item, armor keeps the modlist type), `TakeModlistItemType` (the stats keep their type, the look is forwarded anyway) or `SkipMerging` (the item is not changed and the log names it). Wide changes are always skipped. |
| **Verbose logging** | off | Log every item, field and asset decision. |
| **Content merging → Allow merging** | on | Self-contained patch and asset copying, as described above. |
| **Content merging → Forward folder name** | `OUTPUT ItemLookMerger` | The MO2 mod folder (directly in the mods folder) that receives the assets and CREDITS.md. Its content is replaced on every run, so use it for nothing else. |
