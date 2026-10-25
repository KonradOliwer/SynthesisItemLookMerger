using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace ItemLookMerger.Tests.Merging;

/// <summary>
/// In-memory plugins: Base.esm adds an iron cuirass and a sword, Stats.esp changes their stats, and look plugins change their look.
/// </summary>
internal sealed class TestPlugins
{
    public SkyrimMod Base { get; } = NewMod("Base.esm");
    public SkyrimMod Stats { get; } = NewMod("Stats.esp");
    public SkyrimMod Patch { get; } = NewMod("Patch.esp");

    public Armor Cuirass { get; }
    public ArmorAddon BaseAddon { get; }
    public Weapon Sword { get; }

    public TestPlugins()
    {
        BaseAddon = Base.ArmorAddons.AddNew("IronCuirassAA");
        BaseAddon.WorldModel = new GenderedItem<Model?>(new Model { File = @"Armor\Iron\cuirass_m.nif" }, null);

        Cuirass = Base.Armors.AddNew("ArmorIronCuirass");
        Cuirass.Name = "Iron Armor";
        Cuirass.ArmorRating = 25;
        Cuirass.Value = 125;
        Cuirass.Weight = 30;
        Cuirass.WorldModel = new GenderedItem<ArmorModel?>(new ArmorModel { Model = new Model { File = @"Armor\Iron\cuirass_go.nif" } }, null);
        Cuirass.Armature.Add(BaseAddon.ToLink());

        Sword = Base.Weapons.AddNew("IronSword");
        Sword.Name = "Iron Sword";
        Sword.Model = new Model { File = @"Weapons\Iron\sword.nif" };

        var statsCuirass = Stats.Armors.GetOrAddAsOverride(Cuirass);
        statsCuirass.ArmorRating = 40;
        statsCuirass.Value = 500;
        Stats.Weapons.GetOrAddAsOverride(Sword).BasicStats = new WeaponBasicStats { Value = 77 };
    }

    public static SkyrimMod NewMod(string fileName) => new(ModKey.FromFileName(fileName), SkyrimRelease.SkyrimSE);

    /// <summary>
    /// A look plugin: overrides the cuirass with a new ground model, a new armor addon (with an alternate texture set) and a new
    /// name, and also changes its armor rating, which must not be forwarded.
    /// </summary>
    public SkyrimMod LookPlugin(string fileName, string meshName)
    {
        var look = NewMod(fileName);
        var textureSet = look.TextureSets.AddNew($"{meshName}TXST");
        textureSet.Diffuse = $@"Armor\{meshName}\{meshName}_d.dds";

        var addon = look.ArmorAddons.AddNew($"{meshName}AA");
        addon.WorldModel = new GenderedItem<Model?>(
            new Model
            {
                File = $@"Armor\{meshName}\{meshName}_m.nif",
                AlternateTextures = [new AlternateTexture { Name = "Body", Index = 0, NewTexture = textureSet.ToLink() }],
            },
            null);

        var cuirass = look.Armors.GetOrAddAsOverride(Cuirass);
        cuirass.Name = $"{meshName} Armor";
        cuirass.ArmorRating = 99;
        cuirass.WorldModel = new GenderedItem<ArmorModel?>(new ArmorModel { Model = new Model { File = $@"Armor\{meshName}\{meshName}_go.nif" } }, null);
        cuirass.Armature.SetTo([addon.ToLink()]);
        return look;
    }

    /// <summary>Adds a sword override with a new model and a new first person static (owned by <paramref name="look"/>).</summary>
    public Static AddSwordLook(SkyrimMod look)
    {
        var firstPerson = look.Statics.AddNew("SwordFirstPerson");
        firstPerson.Model = new Model { File = @"Weapons\Fancy\sword_1st.nif" };
        var sword = look.Weapons.GetOrAddAsOverride(Sword);
        sword.Model = new Model { File = @"Weapons\Fancy\sword.nif" };
        sword.FirstPersonModel.SetTo(firstPerson);
        sword.BasicStats = new WeaponBasicStats { Value = 1 };
        return firstPerson;
    }

    /// <summary>A plugin that overrides the cuirass and only changes its stats; its look equals Base.esm's.</summary>
    public SkyrimMod StatsOnlyPlugin(string fileName)
    {
        var plugin = NewMod(fileName);
        plugin.Armors.GetOrAddAsOverride(Cuirass).Weight = 1;
        return plugin;
    }

    public IReadOnlyList<ISkyrimModGetter> LoadOrder(params SkyrimMod[] extra) => [Base, Stats, .. extra];
}
