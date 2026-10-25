using System.IO.Abstractions;
using Mutagen.Bethesda.Plugins;

namespace ItemLookMerger.Core.Mo2;

/// <summary>What Synthesis saw of the game: the Data folder, the enabled plugins of its load order and the patch.</summary>
/// <param name="EnabledPlugins">
/// The enabled plugins of Synthesis's full listing (<c>RawLoadOrder</c>) in order, including those after the patch's line (which
/// Synthesis trims from <c>LoadOrder</c>), without the patch and without repeats.
/// </param>
public sealed record LoadOrderFacts(string DataFolder, IReadOnlyList<ModKey> EnabledPlugins, ModKey Patch);

/// <summary>
/// Finds the MO2 instance and profile this run was launched from. MO2 is mandatory: without one the run fails, there is no fallback.
/// <para>
/// MO2 tells a launched program neither its instance nor its profile, so every possible pair is collected and checked against what
/// Synthesis actually listed: the portable instance next to ModOrganizer.exe, the registry's current global instance and every global
/// instance in %LOCALAPPDATA%\ModOrganizer for the same game, each with all its profiles. A pair is kept only when its plugins.txt
/// has exactly Synthesis's enabled plugins active and each of them comes from an enabled mod of the profile or the game's Data folder.
/// The run goes on only when exactly one pair is kept (or several that are the same profile copied, with an identical modlist.txt).
/// </para>
/// </summary>
public static class Mo2Locator
{
    internal const string NotLaunchedThroughMo2 =
        "ItemLookMerger needs Mod Organizer 2: no ModOrganizer.exe was found among the processes that started this patcher. "
        + "Launch Synthesis through MO2 (add it as an executable in MO2 and run it from there).";

    /// <summary>The plugins the game always loads (Skyrim.esm, Update.esm and the DLCs), whatever plugins.txt says.</summary>
    private static readonly HashSet<ModKey> BaseMasters =
        Implicits.Get(Mutagen.Bethesda.GameRelease.SkyrimSE).Listings.ToHashSet();

    public static Mo2Instance Locate(IMo2Environment environment, IFileSystem fileSystem, LoadOrderFacts facts, ILog log)
    {
        var process = environment.FindModOrganizer();
        if (process.ExecutablePath == null)
            throw new RunFailedException(process.Problem == null ? NotLaunchedThroughMo2 : $"{NotLaunchedThroughMo2} {process.Problem}");

        var path = fileSystem.Path;
        var dataFolder = Mo2InstanceFolders.Normalize(path, facts.DataFolder, facts.DataFolder);
        var rejected = new List<string>();
        var matching = new List<(Mo2Instance Instance, string Origin)>();
        foreach (var (instanceFolder, origin) in CandidateInstances(environment, fileSystem, process.ExecutablePath))
        {
            Mo2InstanceFolders folders;
            try
            {
                folders = Mo2InstanceFolders.Read(fileSystem, instanceFolder);
            }
            catch (RunFailedException ex)
            {
                rejected.Add($"{instanceFolder} ({origin}): {ex.Message}");
                continue;
            }
            if (!string.Equals(folders.GameDataDirectory, dataFolder, StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add($"{instanceFolder} ({origin}): it is for the game at {folders.GameDataDirectory}, not {dataFolder}.");
                continue;
            }
            var pluginHolders = PluginHolders(fileSystem, folders);
            foreach (var profile in folders.ProfileNames(fileSystem))
            {
                var instance = Mo2Instance.Load(fileSystem, folders, profile);
                var mismatch = Mismatch(instance, facts, pluginHolders, fileSystem, dataFolder);
                if (mismatch == null) matching.Add((instance, origin));
                else rejected.Add($"{instanceFolder} ({origin}), profile \"{profile}\": {mismatch}");
            }
        }

        // Copies of one profile (same instance, same modlist.txt) give the same result, so any of them will do.
        var distinct = matching
            .GroupBy(match => (Folder: match.Instance.InstanceDirectory.ToUpperInvariant(), match.Instance.ModListText))
            .ToList();
        if (distinct.Count == 1)
        {
            var (instance, origin) = distinct[0].First();
            log.Info($"MO2 instance: {instance.InstanceDirectory} ({origin}), profile \"{instance.ProfileName}\".");
            if (distinct[0].Count() > 1)
                log.Info($"Profiles {string.Join(", ", distinct[0].Select(match => $"\"{match.Instance.ProfileName}\""))} match equally (identical mod lists).");
            foreach (var reason in rejected) log.Verbose($"MO2 candidate rejected: {reason}");
            log.Verbose($"MO2 {instance.DescribeModSearch()}.");
            return instance;
        }

        var listing = string.Join("\n  ", rejected.Select(reason => "rejected: " + reason)
            .Concat(matching.Select(match => $"matches: {match.Instance.InstanceDirectory} ({match.Origin}), profile \"{match.Instance.ProfileName}\"")));
        throw new RunFailedException(
            (matching.Count == 0
                ? "No MO2 instance and profile matches the load order Synthesis was given, so the patcher cannot tell which mods are yours."
                : "Several MO2 profiles match the load order Synthesis was given, and they differ, so the patcher cannot tell which one is running.")
            + (listing.Length > 0 ? "\n  " + listing : " No MO2 instance was found at all."));
    }

    /// <summary>Instance folders to try, each with how it was found; duplicates are dropped.</summary>
    private static IEnumerable<(string Folder, string Origin)> CandidateInstances(IMo2Environment environment, IFileSystem fileSystem, string executable)
    {
        var path = fileSystem.Path;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var executableFolder = path.GetDirectoryName(executable);
        if (executableFolder != null && fileSystem.File.Exists(path.Combine(executableFolder, "ModOrganizer.ini")) && seen.Add(path.GetFullPath(executableFolder)))
            yield return (executableFolder, "portable instance next to ModOrganizer.exe");

        var localAppData = environment.LocalAppData;
        if (string.IsNullOrWhiteSpace(localAppData)) yield break;
        var globalRoot = path.Combine(localAppData, "ModOrganizer");
        var current = environment.CurrentGlobalInstance();
        if (!string.IsNullOrWhiteSpace(current))
        {
            var currentFolder = path.Combine(globalRoot, current);
            if (fileSystem.File.Exists(path.Combine(currentFolder, "ModOrganizer.ini")) && seen.Add(path.GetFullPath(currentFolder)))
                yield return (currentFolder, "MO2's current global instance");
        }
        if (!fileSystem.Directory.Exists(globalRoot)) yield break;
        foreach (var folder in fileSystem.Directory.EnumerateDirectories(globalRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (fileSystem.File.Exists(path.Combine(folder, "ModOrganizer.ini")) && seen.Add(path.GetFullPath(folder)))
                yield return (folder, "global instance");
        }
    }

    /// <summary>For each plugin file name, the mods (Overwrite included) that have it, read once per instance.</summary>
    private static Dictionary<string, List<string>> PluginHolders(IFileSystem fileSystem, Mo2InstanceFolders folders)
    {
        var holders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Scan(string folder, string modName)
        {
            if (!fileSystem.Directory.Exists(folder)) return;
            foreach (var file in fileSystem.Directory.EnumerateFiles(folder))
            {
                var name = fileSystem.Path.GetFileName(file);
                if (!Mo2Instance.PluginExtensions.Contains(fileSystem.Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) continue;
                if (!holders.TryGetValue(name, out var mods)) holders[name] = mods = [];
                mods.Add(modName);
            }
        }
        if (fileSystem.Directory.Exists(folders.ModsDirectory))
        {
            foreach (var modFolder in fileSystem.Directory.EnumerateDirectories(folders.ModsDirectory))
                Scan(modFolder, fileSystem.Path.GetFileName(modFolder));
        }
        Scan(folders.OverwriteDirectory, Mo2Instance.OverwriteName);
        return holders;
    }

    /// <summary>
    /// Why the profile does not match the load order, or null when it does: the profile's active plugins and Synthesis's enabled
    /// plugins (its full listing, so also those below the patch's line) must be the same plugins in the same order. The patch is
    /// left out on both sides, as are the base masters and the plugins no mod holds (the game's own, Creation Club), which
    /// Synthesis adds or drops itself.
    /// </summary>
    private static string? Mismatch(
        Mo2Instance instance, LoadOrderFacts facts, Dictionary<string, List<string>> holders, IFileSystem fileSystem, string dataFolder)
    {
        if (instance.Plugins == null) return "it has no plugins.txt.";
        var enabledMods = instance.Mods.Where(mod => mod.Enabled).Select(mod => mod.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Compared(ModKey plugin) => plugin != facts.Patch && !BaseMasters.Contains(plugin) && holders.ContainsKey(plugin.FileName);
        var active = instance.ActivePlugins(facts.Patch);
        var profile = active.Where(Compared).ToList();
        var synthesis = facts.EnabledPlugins.Where(Compared).Distinct().ToList();

        var notLoaded = profile.Except(synthesis).ToList();
        if (notLoaded.Count > 0) return $"its plugins.txt has {List(notLoaded)} active, which Synthesis's load order does not.";
        var notActive = synthesis.Except(profile).ToList();
        if (notActive.Count > 0) return $"{List(notActive)} {(notActive.Count == 1 ? "is" : "are")} in Synthesis's load order but not active in this profile.";
        var reordered = profile.Zip(synthesis)
            .Where(pair => pair.First != pair.Second)
            .Select(pair => $"{pair.First} where Synthesis has {pair.Second}")
            .FirstOrDefault();
        if (reordered != null) return $"its plugins.txt has the same plugins active, in another order ({reordered}).";

        foreach (var plugin in active)
        {
            if (holders.TryGetValue(plugin.FileName, out var mods))
            {
                if (!mods.Any(enabledMods.Contains)) return $"{plugin} is only in disabled mods of this profile ({string.Join(", ", mods)}).";
            }
            else if (!fileSystem.File.Exists(fileSystem.Path.Combine(dataFolder, plugin.FileName)))
            {
                return $"{plugin} is in no mod of this instance and not in the game's Data folder.";
            }
        }
        return null;
    }

    private static string List(IEnumerable<ModKey> plugins)
    {
        var names = plugins.Select(plugin => plugin.FileName.String).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return string.Join(", ", names.Take(5)) + (names.Count > 5 ? $" (and {names.Count - 5} more)" : "");
    }
}
