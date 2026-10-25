using ItemLookMerger.Core.Credits;
using ItemLookMerger.Core.Merging;
using ItemLookMerger.Core.Mo2;
using Mutagen.Bethesda.Plugins;

namespace ItemLookMerger.Tests.Credits;

public class CreditsTests
{
    private static readonly ModKey Skyrim = ModKey.FromFileName("Skyrim.esm");
    private static readonly ModKey Look = ModKey.FromFileName("BetterIron.esp");
    private static readonly ModKey Stats = ModKey.FromFileName("StatsOverhaul.esp");
    private static readonly ModKey Swords = ModKey.FromFileName("Swords.esp");

    [Fact]
    public void Credits_match_the_snapshot()
    {
        var changes = new[]
        {
            new ChangedItem("ARMO", new FormKey(Skyrim, 0x012E49), "ArmorIronCuirass", "Iron Armor", [Look], Stats, ["WorldModel", "Armature"]),
            // The sword's own model from Swords.esp, its first person static from BetterIron.esp.
            new ChangedItem("WEAP", new FormKey(Skyrim, 0x012EB7), "IronSword", "Iron | Sword", [Swords, Look], Skyrim, ["Model", "STAT IronSword1st"]),
        };
        var origins = new Dictionary<ModKey, PluginOrigin>
        {
            [Look] = new(Look, "Better Iron Armor", new ModMeta("12345", "1.2.0", "Better Iron Armor-12345-1-2-0.7z", "Nexus", "")),
            [Stats] = new(Stats, "Stats Overhaul", null),
            [Swords] = new(Swords, "Sword Pack", new ModMeta("", "2.0", "swords.zip", "", "https://example.org/swords")),
            [Skyrim] = new(Skyrim, null, null),
        };

        var text = CreditsWriter.Build(changes, plugin => origins[plugin]);

        var snapshot = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CREDITS.snapshot.md"));
        Assert.Equal(Normalize(snapshot), Normalize(text));
    }

    [Fact]
    public void A_weapon_that_took_its_type_from_a_base_item_credits_that_item_and_the_plugin_its_stats_come_from()
    {
        var baseItem = new BaseItem("IronGreatsword", new FormKey(Skyrim, 0x013986), Swords);
        var changes = new[]
        {
            new ChangedItem(
                "WEAP", new FormKey(Skyrim, 0x012EB7), "UniqueBlade", "Blade", [Look], Stats, ["Model", "weapon type from IronGreatsword"], Base: baseItem),
        };
        var origins = new Dictionary<ModKey, PluginOrigin>
        {
            [Look] = new(Look, "Better Iron Armor", null),
            [Stats] = new(Stats, "Stats Overhaul", null),
            [Swords] = new(Swords, "Sword Pack", null),
        };

        var text = CreditsWriter.Build(changes, plugin => origins[plugin]);

        Assert.Contains(
            "| UniqueBlade | Blade | `012EB7:Skyrim.esm` | BetterIron.esp (Better Iron Armor) | "
            + "StatsOverhaul.esp (Stats Overhaul); weapon type stats from IronGreatsword `013986:Skyrim.esm` (Swords.esp (Sword Pack)) |",
            text);
        Assert.Contains("Swords.esp", text[text.IndexOf("## Mods", StringComparison.Ordinal)..]);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
