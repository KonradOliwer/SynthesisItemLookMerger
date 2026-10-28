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
/// <param name="BodySlideOutputs">Assets a BodySlide project builds that another mod provides; they are left to it, without a warning.</param>
public sealed record AssetCopyResult(int Copied, int BaseGameSkipped, int FromOtherMods, int Missing = 0, int BodySlideOutputs = 0);

/// <summary>
/// Step 6: copies the assets of the look sources (enabled or not; never the stats side's) from their MO2 mod (loose files, then the
/// plugin's BSAs) into the forward folder. When several look sources need the same path, the one whose mod has the higher MO2 priority
/// provides it. A copied NIF adds the textures, SMP configs and .tri files it uses, and the BodySlide projects that build it (the
/// mesh itself is left out when another enabled mod provides it). A path no look source's mod has is never copied: a base-game asset (even one
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
    private readonly Dictionary<string, BodySlideProjects> _bodySlide = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The BodySlide files written so far, with the MO2 priority of the mod they came from (a higher-priority mod replaces them).</summary>
    private readonly Dictionary<string, int> _bodySlideCopied = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lookMods = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (BodySlideSet Set, string Mod)>? _bodySlideOutputs;
    /// <summary>The paths left to the BodySlide output, by either rule, each counted once.</summary>
    private readonly HashSet<string> _leftToBodySlide = new(StringComparer.OrdinalIgnoreCase);

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
        IFileSystem fileSystem, ILog log, Func<string, bool>? isVanilla = null, string? targetFolder = null,
        IReadOnlyList<LookSource>? lookSources = null) =>
        new AssetCopier(mo2, forwardFolder, dataFolder, loadOrder, fileSystem, log, isVanilla, targetFolder).CopyAll(needs, lookSources ?? []);

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

    private AssetCopyResult CopyAll(IReadOnlyList<AssetNeed> needs, IReadOnlyList<LookSource> lookSources)
    {
        foreach (var source in lookSources.Concat(needs.Select(need => need.Source)))
            if (source.ModName != null) _lookMods.Add(source.ModName);
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
                    copied += provider.Copied;
                    next.AddRange(provider.References.Select(reference => provider.Need with { Path = reference }));
                    continue;
                }
                switch (Classify(path, first))
                {
                    case Origin.Vanilla: baseGame++; break;
                    case Origin.OtherMod: otherMods++; break;
                    case Origin.BodySlide: _leftToBodySlide.Add(path); break;
                    default: missing++; break;
                }
            }
            wave = next;
        }
        return new AssetCopyResult(copied, baseGame, otherMods, missing, _leftToBodySlide.Count);
    }

    /// <param name="References">The paths the copied NIF points at (textures, SMP configs, .tri files), to copy in turn.</param>
    /// <param name="Copied">The files written, with any BodySlide files; 0 for a mesh left to the BodySlide output.</param>
    private sealed record Provided(AssetNeed Need, IReadOnlyList<string> References, int Copied);

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
        var isNif = path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase);
        var projects = isNif ? BodySlideFor(winner.Source) : null;
        var sets = projects?.Producing(path) ?? [];
        var count = 0;
        // A mesh a BodySlide project builds is left to the BodySlide output: a loose file of an enabled mod (Overwrite too) that is
        // neither a look mod nor the forward folder.
        var builtBy = sets.Count == 0 ? null : _mo2.FindEnabledModWithFile(path, [.. _lookMods, _forwardFolderName]);
        if (builtBy != null) _leftToBodySlide.Add(path);
        if (builtBy == null)
        {
            WriteFile(target, bytes!);
            count++;
            _log.Verbose($"Asset {path}: copied from MO2 mod \"{winner.Source.ModName}\".");
        }
        if (sets.Count > 0)
        {
            var projectNames = string.Join(", ", sets.Select(set => $"\"{set.Name}\"").Distinct());
            _log.Info(builtBy != null
                ? $"Mesh {path} is built by BodySlide project {projectNames}; it is left to the BodySlide output in MO2 mod \"{builtBy.Name}\" and the look mod's prebuilt mesh is not copied."
                : $"Mesh {path} was copied prebuilt from MO2 mod \"{winner.Source.ModName}\"; you can rebuild it with BodySlide project {projectNames}.");
            count += CopyBodySlideFiles(winner, projects!, sets);
        }
        return new Provided(winner, isNif ? NifReferences.Read(bytes!) : [], count);
    }

    private void WriteFile(string target, byte[] bytes)
    {
        _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(target)!);
        _fileSystem.File.WriteAllBytes(target, bytes);
    }

    /// <summary>The look source's mod's BodySlide projects (read once per mod), or null when its plugin has no MO2 mod.</summary>
    private BodySlideProjects? BodySlideFor(LookSource source)
    {
        return source.ModName == null ? null : BodySlideOf(source.ModName);
    }

    /// <summary>A mod's BodySlide projects, read (and warned about) once per run.</summary>
    private BodySlideProjects BodySlideOf(string modName)
    {
        if (!_bodySlide.TryGetValue(modName, out var projects))
        {
            projects = BodySlideProjects.Read(_fileSystem, _mo2.ModFolder(modName), modName, _log);
            _bodySlide[modName] = projects;
        }
        return projects;
    }

    /// <summary>
    /// Every mesh and .tri file the slider sets of all enabled mods (look mods or not; Overwrite included, the forward folder not) may
    /// build, with the set and the mod of its .osp. Built once, on first use; the highest-priority mod's set is kept for a shared path.
    /// </summary>
    private Dictionary<string, (BodySlideSet Set, string Mod)> BodySlideOutputs()
    {
        if (_bodySlideOutputs != null) return _bodySlideOutputs;
        _bodySlideOutputs = new Dictionary<string, (BodySlideSet, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in _mo2.Mods.Where(mod => mod.Enabled && !string.Equals(mod.Name, _forwardFolderName, StringComparison.OrdinalIgnoreCase)))
            foreach (var set in BodySlideOf(mod.Name).Sets)
                foreach (var output in set.Outputs.Append(set.Tri))
                    _bodySlideOutputs.TryAdd(AssetPath.Normalize(output), (set, mod.Name));
        return _bodySlideOutputs;
    }

    /// <summary>
    /// Copies the slider sets' .osp files, their ShapeData folders and the SliderGroups naming them; returns the number of files newly
    /// written. A file two look mods both have comes from the higher-priority mod, whichever is processed first.
    /// </summary>
    private int CopyBodySlideFiles(AssetNeed need, BodySlideProjects projects, IReadOnlyList<BodySlideSet> sets)
    {
        var files = new List<string>();
        foreach (var set in sets)
        {
            files.Add(set.Project);
            if (set.DataFolder != null) files.AddRange(projects.ShapeDataFiles(set.DataFolder));
            files.AddRange(projects.Groups.Where(group => group.Members.Contains(set.Name)).Select(group => group.Path));
        }

        var count = 0;
        foreach (var file in files)
        {
            var key = AssetPath.Key(file);
            var written = _bodySlideCopied.TryGetValue(key, out var writtenPriority);
            if (written && writtenPriority >= need.Source.ModPriority) continue;
            var bytes = SourceFolder(need.Source)?.Read(file);
            var target = AssetPath.Inside(_fileSystem, _targetFolder, file);
            if (bytes == null || target == null) continue;
            WriteFile(target, bytes);
            _bodySlideCopied[key] = need.Source.ModPriority;
            _log.Verbose($"BodySlide file {file}: copied from MO2 mod \"{need.Source.ModName}\".");
            if (!written) count++;
        }
        return count;
    }

    /// <summary>
    /// What provides the built BodySlide output: a loose file of an enabled mod (Overwrite too) or a BSA of one, neither a look mod nor
    /// the forward folder, or a loose file in the game's Data folder; null when nothing does.
    /// </summary>
    private string? BuiltOutputProvider(string path)
    {
        string?[] except = [.. _lookMods, _forwardFolderName];
        if (_mo2.FindEnabledModWithFile(path, except) is { } looseMod) return $"MO2 mod \"{looseMod.Name}\"";
        if (_dataFolder.LooseExists(path)) return "the game's Data folder";
        return _mo2.FindEnabledModWithArchivedFile(path, except) is { } bsaMod ? $"a BSA of MO2 mod \"{bsaMod.Name}\"" : null;
    }

    private enum Origin { Vanilla, OtherMod, BodySlide, Missing }

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
        var isOutputCandidate = path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tri", StringComparison.OrdinalIgnoreCase);
        if (isOutputCandidate && BodySlideOutputs().TryGetValue(AssetPath.Normalize(path), out var built))
        {
            // Expected BodySlide output: no warning when some enabled mod that is not a look mod (or the Data folder) already has the built file.
            if (BuiltOutputProvider(path) is { } provider)
            {
                _log.Verbose($"Asset {path}: not in a look source's mod; it is an output of BodySlide project \"{built.Set.Name}\" (MO2 mod \"{built.Mod}\") and is left to {provider}.");
                return Origin.BodySlide;
            }
            // A look mod's own prebuilt file is no build; a .tri the set did not write, while its mesh is built, is just missing.
            var lookModHasIt = looseMod is { Enabled: true } || _mo2.FindEnabledModWithArchivedFile(path, _forwardFolderName) != null;
            var triNotWritten = path.EndsWith(".tri", StringComparison.OrdinalIgnoreCase)
                && built.Set.Outputs.Any(output => BuiltOutputProvider(output) != null);
            if (!lookModHasIt && !triNotWritten)
            {
                _log.Warning($"{need.Item}: asset {path} {notIn} and was not copied; it is an output of BodySlide project \"{built.Set.Name}\" (MO2 mod \"{built.Mod}\") that has not been built yet. Build it with BodySlide.");
                return Origin.Missing;
            }
        }
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
        var sameName = SourceFolder(need.Source)?.FindByFileName(path[(path.LastIndexOf('\\') + 1)..]) ?? [];
        if (sameName.Count > 0)
        {
            var shown = string.Join(", ", sameName.Take(3).Select(found => $"\"{found}\""));
            if (sameName.Count > 3) shown += $" and {sameName.Count - 3} more";
            _log.Warning(
                $"{need.Item}: asset {path} {notIn}, and no other MO2 mod or vanilla archive has it, but the mod has a file with the same name at {shown}. "
                + "This is likely a packaging error in the look mod: the mesh points to a path the mod doesn't ship, so the file is not copied "
                + "and the mod behaves the same with or without the patcher.");
            return Origin.Missing;
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
