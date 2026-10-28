# ItemLookMerger

A Synthesis patcher for Skyrim Special Edition that combines the **look** of items from look mods with the **stats** from
whichever plugin wins them otherwise. Look mods no longer have to win the record, and no SkyPatcher is needed.

## How it works

**Items covered:** armor, weapons, ammunition, misc items, books, ingredients, potions and food, soul gems, keys.

**Look** (taken from the look plugins): world and inventory models with their alternate textures, icons, object bounds, armor
addons, first-person and scope models, book inventory art, and the name when **Forward names** is on.

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

A wide change is not merged: the item is left untouched, and the log names it as an error with both types and both plugins. The setting only decides narrow changes:

- `TakeVisualModWeaponType` (default): a weapon takes the type-related stats of a base item (below); an armor keeps your modlist's type
  and the look is forwarded. The log counts such armors in one line, and lists them with Verbose logging.
- `TakeModlistItemType`: the stats side keeps its type and the look is forwarded.
- `SkipMerging`: the item is left untouched and the log names it with both types and both plugins.

#### Base item

The base item is a generic weapon of the look's type and of the stats side's material, so the plain greatsword of the same material
for a one-handed sword that looks like a greatsword.

The look's type, animation type included, always comes from the visual mod. A look record whose animation type contradicts its own
weapon-type keywords is inconsistent: it has at least one known vanilla type keyword and none of its known ones is expected for its
animation type. Custom `WeapType...` keywords (WeapTypeRapier, WeapTypeKatana, ...) are ignored, and a look with a keyword that cannot
be resolved is not called inconsistent. The expected keywords are (OneHandSword `WeapTypeSword`, OneHandDagger `WeapTypeDagger`, OneHandAxe `WeapTypeWarAxe`, OneHandMace `WeapTypeMace`, TwoHandSword
`WeapTypeGreatsword`, TwoHandAxe `WeapTypeBattleaxe` or `WeapTypeWarhammer`, Bow `WeapTypeBow`, Staff `WeapTypeStaff`). Crossbows are not
checked, and a look without a known weapon-type keyword is not inconsistent (it keeps the normal base lookup). Such an item is reported as an error (item, look plugin,
animation type and the contradicting keywords) and is not merged: it keeps the modlist winner's version. Nothing is guessed and
there is no fallback. This applies only where a base item is looked for, that is `TakeVisualModWeaponType` on an item whose animation
type differs from the stats side's (also for items a look plugin added when `CopyAddedItems` copies them).

1. The look record's **template** is used when it is such a weapon itself.
2. Otherwise the best weapon that passes the same rules. It has the look's animation type, shares a weapon-type keyword
   (`WeapType...`) with the look record, and carries at least one of the stats side's material keywords (`WeapMaterial...`,
   `DLC1WeapMaterial...`, `DLC2WeaponMaterial...`), or of the look record's when the stats side has none; other keywords it has do
   not matter. It is playable, not enchanted, not bound, player-only, undroppable or reserved for non-combat use, without a
   template, and has weapon data and damage and weight. It has to have a weapon-type and a material keyword, and a look plugin must
   not have introduced it or retype it.
3. Among several: the one sharing the most material keywords, then the most weapon-type keywords, then the vanilla masters first
   (Skyrim, Update, Dawnguard, HearthFires, Dragonborn), then weapons without scripts, then the plugin earlier in the load order,
   then the lowest FormID.

A base item's stats are those its own stats side has (an earlier patcher's version before this run, else the winning non-look
plugin), so they do not depend on the order items are processed. When a look plugin is the only plugin overriding the base item,
its record supplies the base's stats and is credited. With no base item (no weapon-type or material keyword to match, or no such
weapon), the item is left untouched and the log warns.

**Taken from the base item:** the weapon data (animation type, speed, reach, sight FOV, VATS chance, attack animation, projectiles,
embedded actor value, range, on-hit, attack multiplier, rumble, skill, stagger, type flags), damage and weight, critical data (none
when the base has none), equipment type, block and bash and alternate block impact data, the pickup, putdown, attack, idle, equip
and unequip sounds, detection sound level, and the weapon-type and material keywords.

**Kept from the stats side:** name, value, enchantment, scripts, description, destructible, template, resist, the
item flags (ignores normal weapon resistance, can't drop, non-playable, player only, minor crime, non-hostile, bound weapon) and all
other keywords. The look fields come from the look record.

### Content merging on (default)

The patch is self-contained: it needs none of the look plugins as a master, so **you can disable the look mods after the run** and
keep their looks (enable them again for the next run).

- The forms the forwarded looks depend on are in the patch as new records (see above), including those the look plugins added.
- At the end of the log, each look plugin states what the patch carries of it and what disabling it drops. What a plugin
  **supplied** is counted: the item looks it won (the item's look fields in the patch are its version), with Copy added items the
  added items whose copied version is its (the latest look plugin containing the item), and the forms the looks depend on whose
  copied version is its; an added item also counts for the plugin that added it when another plugin's version was copied. A
  plugin that supplied none of these is a warning (contributes nothing), unless it adds items (one of the item types, so a plugin
  adding only recipes still gets the warning). Then it is an information line: with Copy added items off, that its added items are
  not copied because the setting is off; with it on, that none of its added items could be copied (the errors say why).
- Only these visual records are carried. A field outside the look that points at a look plugin's own record (a keyword, an
  enchantment, a script, ...) keeps the stats side's value instead, and the log names the item and field.
- The look mods' meshes and textures are copied into the forward folder (see Assets).

### Copy added items

Off by default, and only with merging on (with merging off it is ignored and the log warns: the look plugins stay enabled, so the
items would exist twice). It moves the items a look plugin **adds itself** (new records with no original item elsewhere: armor,
weapons, ammunition and the other item types above) into the patch, so that you can disable that look mod.

- Every such item is copied as a **new record with a new FormID** and the EditorID rule above (`_ILM`, plus the plugin and FormID
  on a clash). Its armor addons, texture sets and the other forms its look depends on are copied the same way, and the links
  are pointed at the copies, as are links to other copied added items (an armor's template armor, an enchanted item's template).
  The log has one line per copied item with its original FormKey and EditorID and its new FormKey.
- Where several look plugins contain the item (one adds it, another overrides it), the one later in the load order provides the
  copy, and there is only one copy. The copy never takes the EditorID of the item it is made from: when that already ends in
  `_ILM`, the plugin and FormID are added.
- **Stats side:** when a non-look plugin overrides the item, or an earlier Synthesis patcher already put a version of it in the
  patch, that version is the winner. The copy is the winner's version with the look plugin's visual fields, and the winner goes
  through the same type discrepancy rules as an override (see Type discrepancies): a wide change, or the setting `SkipMerging`,
  leaves the item uncopied with the usual log line. An earlier patcher's override of the look plugin's item is replaced by the copy,
  since the patch must not need the look plugin; when the item is not copied (a type discrepancy, a required link below), that
  override stays as it is, and since it still needs the look plugin as a master, the self-contained check then stops the run with a
  message naming that record. In CREDITS.md the stats of such a copy are credited to "earlier patcher".
- The meshes, textures and files named inside them are copied like all look assets (see Assets).
- **Not copied:** everything that is not one of the item types, so recipes, leveled lists, quests, outfits, containers, head parts,
  enchantments, perks and so on. The copied items can only be obtained by console or other means, not in the game world. The
  log lists what disabling each look plugin drops.
- **Saves:** items already in a save stay tied to the original plugin, since the copies have other FormIDs. They do not become the
  copies, and disabling the look plugin breaks them in that save.
- **Winning override:** when a non-look plugin overrides an added item (a balance patch, say), the copy is that plugin's version
  with the look plugin's visual fields (the look fields above), and the log warns that the plugin still needs the look plugin as a
  master, so the look plugin cannot be disabled while it is enabled. Its links into the look plugin that the patch cannot carry
  are cleared (below).
- **Links into the look plugin:** a link in a copied item (or a form it depends on) to a record the patch cannot carry (an
  enchantment, keyword, race, magic effect, script property or head part the look plugin adds) never leaves an invalid record. Each
  case has a warning that names the item and field:
  - A link in a list (keywords, additional races, armor addons) is **removed** from the list. A potion's or ingredient's effect
    whose base effect cannot be carried is removed as a whole. Removing entries from a copied form list shifts the indexes of the
    entries after them, which matters to a script that reads the list by index.
  - A cleared enchantment (ARMO, WEAP) also clears the enchantment amount.
  - The race of an armor or armor addon falls back to the vanilla DefaultRace (Skyrim.esm 000019).
  - A single link that is required (one Mutagen does not allow to be empty, such as an arrow's projectile) cannot be cleared: the
    item is **not copied** and the log has an error naming the item and the link. The same for a potion or ingredient whose effects
    all point at the look plugin.
  - Any other single link is set to empty.

  A link to a form the patch carries points at its copy. As with merging on, the run fails if any link to a look plugin is left in
  the patch.

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
- A copied mesh also brings the files it names inside: the SMP physics config of its `HDT Skinned Mesh Physics Object` data (a path
  relative to Data) and the `.tri` morph file of its `BODYTRI` data (a path relative to `meshes`). They are copied from the look
  mod like textures, and the same rules below apply to them.
- BodySlide: when a look mod's `CalienteTools\BodySlide\SliderSets\*.osp` project builds a mesh being copied (its output path and
  file, with `_0` and `_1` when it generates weights), the project, its `ShapeData` folder and the `SliderGroups` files that list
  its slider set are copied too. Only loose files count (BSAs hold none), and a project file that is not valid XML is skipped with a
  warning.
- A mesh that such a project builds is left out when the BodySlide output provides it. The BodySlide output is a loose file in an
  enabled MO2 mod, or in Overwrite, that is not a look mod (any mod holding a listed look plugin) and not the forward folder; a mesh
  in a BSA or in a disabled mod does not count. The log names the mod. Its textures and the files it names are still copied. When
  nothing provides it, the look mod's prebuilt mesh is copied and the log says you can rebuild it with BodySlide.
- When two look mods have the same BodySlide project or `ShapeData` file, the one from the higher-priority look mod is copied.
- Base-game files (the game's and its DLCs' and Creation Club archives) are skipped, also when a replacer mod overrides them. Files
  that only another mod provides are not copied, and the log warns and says where they come from.
- When two look mods have the same file, the one with the higher priority in MO2's left panel provides it.
- The new content is written completely before it replaces the old, so a failed run keeps the previous output. The patcher only
  manages a folder that holds its hidden `.itemlookmerger` marker file, or one that is empty (it then takes it over). Any other
  folder is never touched: the run fails and says so.
- A run that fails (the patcher reports an error) leaves the forward folder as it was (a folder it created, or found empty, is
  removed or emptied again) and removes its own temporary folder (`.itemlookmerger-staging`), and Synthesis does not write the patch
  plugin when the patcher fails, so the earlier patch stays too. The failure message names the step that failed and says whether there was a previous output. If
  something could not be undone, the log and the message say what is left and where.
- The swap moves the previous output to `.itemlookmerger-previous` first, then moves the new content in. If that fails, the new
  entries are removed again and the previous output is put back, and `.itemlookmerger-previous` is deleted only after every entry is
  back. If it cannot be put back, `.itemlookmerger-previous` is kept, an error names it and says to delete it and run the patcher
  again (it may hold only part of the earlier output), and the run fails. Once the new content is in place the run counts as a success: if deleting `.itemlookmerger-previous` or the staging folder
  then fails, the log only warns and names the leftover; the run is never rolled back or failed for that.
- After a crash or a kill, nothing is repaired automatically. The next run refuses to start while `.itemlookmerger-previous` exists
  (a file of that name counts too; also after a failed cleanup) and changes nothing. To continue, delete
  `.itemlookmerger-previous` and run the patcher again, which rebuilds the whole forward folder from the look mods. Do not move
  `.itemlookmerger-previous` back: it may hold only an incomplete copy of the earlier output (the swap or the cleanup was
  interrupted half-way). Look through it only if you want to salvage a file. A leftover staging folder is never part of the
  output; it is removed at the start of the first run that is not blocked by a leftover `.itemlookmerger-previous`.
- The patcher owns the forward folder's content and replaces it on every run, except MO2's `meta.ini` (category, notes, version),
  which is never moved, replaced or deleted. A folder holding only `meta.ini` (MO2's "create empty mod") counts as empty and is
  taken over. Any other file you put there is replaced with the next run.
- Staging adds the folder name `.itemlookmerger-staging` to every path while it is written, so a path that fits finally can fail
  while being written; the run then fails and rolls back.
- If Synthesis itself fails while writing the patch after a successful run, the forward folder is already updated.

Enable the forward folder in MO2 and give it a high priority.

## CREDITS.md

One row per changed item: its EditorID, name and FormKey, the look plugins it takes its look from (the item's own and those of the
forms its look depends on) and the plugin its stats come from, each with its MO2 mod. A weapon that took its type from a base item names that
base item and the plugin its stats came from. Below that, each mod's Nexus link, version and download file as recorded in MO2;
base-game plugins and mods without a `meta.ini` are listed by plugin name only. With Copy added items, a second table lists the copied
added items: EditorID, name, original and new FormKey, the look plugins, and the plugin their stats come from. It is written to the forward folder with merging
on; with merging off none is written, as the look mods stay in the modlist.

## MO2 instance and profile

MO2 does not tell the patcher which instance and profile it runs in, so the patcher takes the only one whose active plugins are
exactly those Synthesis listed, in the same order, each coming from an enabled mod or the game's Data folder. Synthesis hands the
patcher only the plugins above the patch, so only the profile's plugins up to the patch's position in plugins.txt are compared; the
plugins below it are ignored. When plugins.txt does not list the patch yet (a first run), all of them are compared. Copies of one profile with an identical mod list count as one.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| **Plugins to forward** | empty | The look plugins. Each must be active, above the Synthesis patch, when the patcher runs. |
| **Forward names** | off | Also take item names from the look plugins. |
| **In case of type discrepancy** | `TakeVisualModWeaponType` | What to do when the look's weapon or armor type differs narrowly from the stats (see Type discrepancies): `TakeVisualModWeaponType` (weapons take the type stats of a generic base item, armor keeps the modlist type), `TakeModlistItemType` (the stats keep their type, the look is forwarded anyway) or `SkipMerging` (the item is not changed and the log names it). Wide changes are always skipped. |
| **Verbose logging** | off | Log every item, field and asset decision. |
| **Content merging → Allow merging** | on | Self-contained patch and asset copying, as described above. |
| **Content merging → Copy added items** | off | Also move the items a look plugin adds itself into the patch as new records (see Copy added items). Needs Allow merging; ignored with a warning otherwise. |
| **Content merging → Forward folder name** | `OUTPUT ItemLookMerger` | The MO2 mod folder (directly in the mods folder) that receives the assets and CREDITS.md (used only with merging on). Its content is replaced on every run, so use it for nothing else. |

## Log

Synthesis has two log levels for a patcher: standard output is information and standard error is error. Errors go to standard
error, so Synthesis shows them at error level (after the run's other lines). Warnings are standard output lines starting with
`Warning:`, in order with the rest of the log; Synthesis shows them at information level.
