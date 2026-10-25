using ItemLookMerger.Core;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Synthesis.Settings;
using Newtonsoft.Json;

namespace ItemLookMerger;

// The property names are the settings.json keys: renaming one resets that setting for every user.
public class Settings
{
    [SynthesisOrder]
    [SynthesisSettingName("Plugins to forward")]
    [SynthesisTooltip(
        "The plugins whose item looks (meshes, icons, armor addons, view models) are forwarded onto the winning stats record.\n"
        + "Each must be enabled (in the load order) when you run the patcher; otherwise the run stops without changing anything.\n"
        + "For an item several of them change, the one later in the load order wins.\n"
        + "With content merging on, the patch doesn't need them afterwards: you can disable them until the next run.")]
    [JsonProperty(ItemConverterType = typeof(EmptyTextAsNullConverter))]
    public List<ModKey> PluginsToForward { get; set; } = [];

    [SynthesisOrder]
    [SynthesisSettingName("Forward names")]
    [SynthesisTooltip("Also take the item name from the look plugin.")]
    public bool ForwardNames { get; set; }

    // Synthesis lists the member names as they are, so they are spelled out; the tooltip explains them.
    [SynthesisOrder]
    [SynthesisSettingName("In case of type discrepancy")]
    [SynthesisTooltip(
        "What to do when a look plugin gives an item another weapon type (one-handed sword to greatsword, sword to axe) or armor type\n"
        + "(light, heavy, clothing) than the winning stats have.\n"
        + "TakeVisualModWeaponType: a weapon takes the type-bound stats (animation type, speed, reach, damage, equip slot, type keywords,\n"
        + "sounds) of the generic base item of the look's type, and keeps its own name, value, enchantment and scripts. Armor keeps the modlist type.\n"
        + "TakeModlistItemType: the item keeps the type of the winning stats and gets the look anyway.\n"
        + "SkipMerging: the item is not changed; the log names it.\n"
        + "Changes between melee, bow, crossbow and staff, between arrow and bolt, and between body armor, shield and jewelry\n"
        + "stop the run with the items listed, whatever this is set to.")]
    public TypeDiscrepancyHandling InCaseOfTypeDiscrepancy { get; set; } = TypeDiscrepancyHandling.TakeVisualModWeaponType;

    [SynthesisOrder]
    [SynthesisSettingName("Verbose logging")]
    [SynthesisTooltip("Logs every record and field decision, and every asset found or missed.")]
    public bool VerboseLogging { get; set; }

    [SynthesisOrder]
    [SynthesisSettingName("Content merging")]
    [SynthesisTooltip("A self-contained patch and the look mods' assets, so the look mods can be disabled after the run.")]
    public ContentMergingSettings ContentMerging { get; set; } = new();
}

public class ContentMergingSettings
{
    [SynthesisOrder]
    [SynthesisSettingName("Allow merging")]
    [SynthesisTooltip(
        "Makes the patch self-contained: the look records (armor addons, texture sets, statics, projectiles) the forwarded looks use\n"
        + "are copied into it, so it has no look plugin as a master and the look mods can be disabled after the run.\n"
        + "Also copies the look mods' meshes and textures into the forward folder; never those of the stats side or of any other mod.\n"
        + "Off: the patch keeps the look plugins as masters (keep them enabled) and no file is copied.")]
    public bool AllowMerging { get; set; } = true;

    [SynthesisOrder]
    [SynthesisSettingName("Forward folder name")]
    [SynthesisTooltip(
        "The MO2 mod folder (directly in the instance's mods folder) that receives the copied assets and CREDITS.md.\n"
        + "Its content is replaced on every successful run with merging on (a failed run leaves it alone), so use it for nothing else;\n"
        + "a folder this patcher did not create is never touched.\n"
        + "A plain folder name: not empty, not \".\" or \"..\", no \\ or /. Enable it in MO2 after the run.")]
    public string ForwardFolderName { get; set; } = "OUTPUT ItemLookMerger";
}
