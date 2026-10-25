using Mutagen.Bethesda.Synthesis.Settings;

namespace ItemLookMerger;

// The property names are the settings.json keys: renaming one resets that setting for every user.
public class Settings
{
    [SynthesisOrder]
    [SynthesisSettingName("Mods to forward")]
    [SynthesisTooltip(
        "The MO2 mods whose item looks (meshes, icons, armor addons, view models) are forwarded onto the winning stats record.\n"
        + "Enter each mod's folder name exactly as MO2's left panel shows it; enabled and disabled mods both work.\n"
        + "Every plugin (.esp, .esm, .esl) at the top of the mod folder is a look source, and the mod's own assets are copied.\n"
        + "For an item several of them change, the mod with the higher MO2 priority wins (load order breaks ties within one mod).\n"
        + "A plugin outside the load order is read from the mod folder when content merging is on.\n"
        + "An unknown name, or a mod without a plugin, fails the run without changing anything.")]
    public List<string> ModsToForward { get; set; } = [];

    [SynthesisOrder]
    [SynthesisSettingName("Forward names")]
    [SynthesisTooltip("Also take the item name from the look plugin.")]
    public bool ForwardNames { get; set; }

    [SynthesisOrder]
    [SynthesisSettingName("Verbose logging")]
    [SynthesisTooltip("Logs every record and field decision, and every asset found or missed.")]
    public bool VerboseLogging { get; set; }

    [SynthesisOrder]
    [SynthesisSettingName("Content merging")]
    [SynthesisTooltip("Reading look plugins that are disabled or missing from the load order.")]
    public ContentMergingSettings ContentMerging { get; set; } = new();
}

public class ContentMergingSettings
{
    [SynthesisOrder]
    [SynthesisSettingName("Allow merging")]
    [SynthesisTooltip(
        "Reads look plugins that are disabled or missing from the load order out of their MO2 mod, and copies the records their looks need into the patch.\n"
        + "Also copies the assets of every listed look mod (enabled or disabled) into the forward folder.\n"
        + "Only the look mods' assets are copied, never those of the stats side or of any other mod.\n"
        + "Off: no plugin is read from MO2 and no asset is copied.")]
    public bool AllowMerging { get; set; } = true;

    [SynthesisOrder]
    [SynthesisSettingName("Forward folder name")]
    [SynthesisTooltip(
        "The MO2 mod folder (directly in the instance's mods folder) that receives the copied assets and CREDITS.md.\n"
        + "It is emptied on every successful run with merging on (a failed run leaves it alone), so use it for nothing else;\n"
        + "a folder this patcher did not create is never emptied.\n"
        + "A plain folder name: not empty, no \"..\", \\ or /. Enable it in MO2 after the run.")]
    public string ForwardFolderName { get; set; } = "OUTPUT ItemLookMerger";
}
