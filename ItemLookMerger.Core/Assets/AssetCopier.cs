using System.IO.Abstractions;
using ItemLookMerger.Core.Merging;
using ItemLookMerger.Core.Mo2;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Noggog;

namespace ItemLookMerger.Core.Assets;

/// <param name="FromOtherMods">Assets another mod (or the game's Data folder, outside the vanilla archives) provides; each is warned about.</param>
/// <param name="Missing">Assets nothing provides; each is warned about.</param>
public sealed record AssetCopyResult(int Copied, int BaseGameSkipped, int FromOtherMods, int Missing = 0);

/// <summary>
/// Step 6: copies the assets of the look sources (enabled or not; never the stats side's) from their MO2 mod (loose files, then the
/// plugin's BSAs) into the forward folder. When several look sources need the same path, the one whose mod has the higher MO2 priority
/// provides it. A copied NIF adds the textures it uses. A path no look source's mod has is never copied: a base-game asset (even one
/// a replacer mod overrides) is skipped silently, anything else is warned about, naming the mod that has it.
/// </summary>
internal sealed class AssetCopier
{
    private readonly Mo2Instance _mo2;
    private readonly string _targetFolder;
    private readonly string _forwardFolderName;
    private readonly IFileSystem _fileSystem;
    private readonly ILog _log;
    private readonly Func<string, bool> _isVanilla;
    private readonly AssetFolder _dataFolder;
    private readonly Dictionary<ModKey, AssetFolder?> _sourceFolders = [];

    /// <param name="dataFolder">The game's Data folder as this run sees it (MO2's virtual view of the enabled mods and the game).</param>
    /// <param name="loadOrder">The load order's plugins, to tell which ones are the game's own (no MO2 mod holds them).</param>
    /// <param name="isVanilla">Replaces the vanilla archive check (tests have no BSA files).</param>
    /// <param name="targetFolder">Where the files are written: the forward folder's staging folder, or the forward folder itself.</param>
    private AssetCopier(
        Mo2Instance mo2, string forwardFolder, string dataFolder, IReadOnlyList<ModKey> loadOrder, IFileSystem fileSystem, ILog log,
        Func<string, bool>? isVanilla, string? targetFolder)
    {
        _mo2 = mo2;
        _targetFolder = targetFolder ?? forwardFolder;
        _forwardFolderName = fileSystem.Path.GetFileName(forwardFolder);
        _fileSystem = fileSystem;
        _log = log;
        _dataFolder = AssetFolder.WithArchives(fileSystem, dataFolder, _ => false);
        _isVanilla = isVanilla ?? VanillaArchives(mo2, dataFolder, loadOrder, fileSystem).ArchivesContain;
    }

    public static AssetCopyResult Copy(
        IReadOnlyList<AssetNeed> needs, Mo2Instance mo2, string forwardFolder, string dataFolder, IReadOnlyList<ModKey> loadOrder,
        IFileSystem fileSystem, ILog log, Func<string, bool>? isVanilla = null, string? targetFolder = null) =>
        new AssetCopier(mo2, forwardFolder, dataFolder, loadOrder, fileSystem, log, isVanilla, targetFolder).CopyAll(needs);

    /// <summary>
    /// The game's real vanilla content: the BSAs in Data that belong to the game's own plugins (Skyrim.esm, the DLCs, Creation Club;
    /// the load order plugins no MO2 mod holds), unless an MO2 mod provides a BSA of that name. MO2's virtual Data folder also shows
    /// every enabled mod's files, so neither loose files nor the other BSAs there count.
    /// </summary>
    private static AssetFolder VanillaArchives(Mo2Instance mo2, string dataFolder, IReadOnlyList<ModKey> loadOrder, IFileSystem fileSystem)
    {
        var gamePlugins = loadOrder.Where(plugin => mo2.FindModWithFile(plugin.FileName) == null).ToList();
        return AssetFolder.WithArchives(
            fileSystem,
            dataFolder,
            archive => gamePlugins.Any(plugin => Archive.IsApplicable(GameRelease.SkyrimSE, plugin, new FileName(archive)))
                && mo2.FindModWithFile(archive) == null);
    }

    private AssetCopyResult CopyAll(IReadOnlyList<AssetNeed> needs)
    {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int copied = 0, baseGame = 0, otherMods = 0, missing = 0;
        var wave = needs.ToList();
        while (wave.Count > 0)
        {
            var next = new List<AssetNeed>();
            foreach (var group in wave.GroupBy(need => AssetPath.Key(need.Path), StringComparer.OrdinalIgnoreCase))
            {
                if (!done.Add(group.Key)) continue;
                var path = group.Key;
                var first = group.First();
                if (!AssetPath.IsSafe(path))
                {
                    _log.Warning($"{first.Item}: asset path \"{first.Path}\" from {first.Source.ModKey} leaves the Data folder; it is not copied.");
                    missing++;
                    continue;
                }

                if (CopyFromLookSource(path, group.ToList()) is { } provider)
                {
                    copied++;
                    next.AddRange(provider.Textures.Select(texture => provider.Need with { Path = texture }));
                    continue;
                }
                switch (Classify(path, first))
                {
                    case Origin.Vanilla: baseGame++; break;
                    case Origin.OtherMod: otherMods++; break;
                    default: missing++; break;
                }
            }
            wave = next;
        }
        return new AssetCopyResult(copied, baseGame, otherMods, missing);
    }

    private sealed record Provided(AssetNeed Need, IReadOnlyList<string> Textures);

    /// <summary>
    /// Copies the path from the highest-priority look source whose mod has it, warning when another look source has different bytes.
    /// Null when no look source's mod has it (or the target would leave the forward folder).
    /// </summary>
    private Provided? CopyFromLookSource(string path, IReadOnlyList<AssetNeed> needs)
    {
        var candidates = needs
            .GroupBy(need => need.Source.ModKey)
            .Select(group => group.First())
            .OrderByDescending(need => need.Source.ModPriority)
            .Select(need => (Need: need, Bytes: SourceFolder(need.Source)?.Read(path)))
            .Where(candidate => candidate.Bytes != null)
            .ToList();
        if (candidates.Count == 0) return null;

        var (winner, bytes) = candidates[0];
        foreach (var (other, otherBytes) in candidates.Skip(1))
        {
            if (!otherBytes!.AsSpan().SequenceEqual(bytes))
                _log.Warning($"Asset {path}: {winner.Source.ModKey}'s mod and {other.Source.ModKey}'s mod both have it, with different content; the higher-priority mod's (\"{winner.Source.ModName}\") is copied.");
        }

        var target = AssetPath.Inside(_fileSystem, _targetFolder, path);
        if (target == null)
        {
            _log.Warning($"{winner.Item}: asset path \"{path}\" would leave the forward folder; it is not copied.");
            return null;
        }
        _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(target)!);
        _fileSystem.File.WriteAllBytes(target, bytes!);
        _log.Verbose($"Asset {path}: copied from MO2 mod \"{winner.Source.ModName}\".");
        var textures = path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ? NifTextures.Read(bytes!) : [];
        return new Provided(winner, textures);
    }

    private enum Origin { Vanilla, OtherMod, Missing }

    /// <summary>Where a path that no look source's mod has comes from; everything but vanilla content is warned about.</summary>
    private Origin Classify(string path, AssetNeed need)
    {
        var notIn = need.Source.ModName == null
            ? $"is not in {need.Source.ModKey}'s files (the plugin has no MO2 mod)"
            : $"is not in {need.Source.ModKey}'s mod \"{need.Source.ModName}\"";
        string Warn(string from) => $"{need.Item}: asset {path} {notIn} and was not copied; {from}.";

        // A base-game path counts as vanilla even when a replacer mod overrides it.
        if (_isVanilla(path))
        {
            _log.Verbose($"Asset {path}: not in a look source's mod, found in the vanilla archives; skipped.");
            return Origin.Vanilla;
        }
        var looseMod = _mo2.FindModWithFile(path, exceptMod: _forwardFolderName);
        if (looseMod is { Enabled: true })
        {
            _log.Warning(Warn($"it comes from MO2 mod \"{looseMod.Name}\""));
            return Origin.OtherMod;
        }
        if (_dataFolder.LooseExists(path))
        {
            _log.Warning(Warn("it is a loose file in the game's Data folder that no MO2 mod provides"));
            return Origin.OtherMod;
        }
        if (_mo2.FindEnabledModWithArchivedFile(path, exceptMod: _forwardFolderName) is { } archiveMod)
        {
            _log.Warning(Warn($"it comes from a BSA of MO2 mod \"{archiveMod.Name}\""));
            return Origin.OtherMod;
        }
        if (looseMod != null)
        {
            _log.Warning(Warn($"only the disabled MO2 mod \"{looseMod.Name}\" has it"));
            return Origin.OtherMod;
        }
        _log.Warning(Warn("no MO2 mod or vanilla archive has it"));
        return Origin.Missing;
    }

    private AssetFolder? SourceFolder(LookSource source)
    {
        if (!_sourceFolders.TryGetValue(source.ModKey, out var folder))
        {
            folder = source.ModName == null ? null : AssetFolder.ForMod(_fileSystem, _mo2.ModFolder(source.ModName), source.ModKey);
            _sourceFolders[source.ModKey] = folder;
        }
        return folder;
    }
}
