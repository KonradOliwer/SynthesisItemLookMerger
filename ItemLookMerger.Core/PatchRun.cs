using System.IO.Abstractions;
using ItemLookMerger.Core.Assets;
using ItemLookMerger.Core.Credits;
using ItemLookMerger.Core.Merging;
using ItemLookMerger.Core.Mo2;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Core;

/// <summary>Everything one run works on; the entry project fills it from the Synthesis state.</summary>
/// <param name="LoadOrder">The enabled plugins of the load order without the patch, lowest priority first (read through the Data folder).</param>
/// <param name="EnabledPlugins">
/// The enabled plugins of Synthesis's full listing (<c>RawLoadOrder</c>) without the patch, also those below the patch's line that
/// <paramref name="LoadOrder"/> lacks. The MO2 profile is verified against them.
/// </param>
/// <param name="DataFolder">The game's Data folder as Synthesis sees it (under MO2, its virtual view of the enabled mods).</param>
public sealed record RunInputs(
    IReadOnlyList<ISkyrimModGetter> LoadOrder,
    IReadOnlyList<ModKey> EnabledPlugins,
    ISkyrimMod Patch,
    string DataFolder,
    MergeOptions Options,
    IMo2Environment Mo2Environment,
    IFileSystem FileSystem,
    ILog Log);

/// <summary>The whole run: find MO2, resolve the sources, forward the looks, copy assets, write the credits and the summary.</summary>
public static class PatchRun
{
    public static MergeResult Run(RunInputs inputs)
    {
        var (options, fileSystem) = (inputs.Options, inputs.FileSystem);
        var log = new CountingLog(inputs.Log);
        log.Info("=== Item Look Merger ===");

        var loadOrderKeys = inputs.LoadOrder.Select(mod => mod.ModKey).ToList();
        // Synthesis's load order stops at the patch, so each profile is compared with its full listing instead.
        var mo2 = Mo2Locator.Locate(inputs.Mo2Environment, fileSystem, new LoadOrderFacts(inputs.DataFolder, inputs.EnabledPlugins, inputs.Patch.ModKey), log);
        // The name is checked now; the folder's content is only replaced once everything has succeeded, so a failed run leaves it alone.
        string? forwardFolder = null;
        if (options.AllowMerging) forwardFolder = ForwardFolder.Resolve(fileSystem, mo2, options.ForwardFolderName);
        else ForwardFolder.ReportLeftover(fileSystem, mo2, options.ForwardFolderName, log);

        // Fails the run for an empty list or a plugin that isn't loaded, so there is always at least one source past this point.
        var sources = SourceResolver.Resolve(options.PluginsToForward, inputs.LoadOrder, inputs.Patch.ModKey, mo2, log);
        // Without merging the look plugins stay, so copied items would be there twice.
        if (options.CopyAddedItems && !options.AllowMerging)
            log.Warning("CopyAddedItems needs Allow merging: the look plugins stay enabled without it, so the items would exist twice. The setting is ignored.");
        var copyAddedItems = options.CopyAddedItems && options.AllowMerging;
        // With merging on, the patch is made independent of the look plugins, so they can be disabled after the run.
        var result = LookMerger.Merge(
            inputs.LoadOrder, inputs.Patch, sources, options.ForwardNames, selfContained: options.AllowMerging, options.OnTypeDiscrepancy, log, copyAddedItems);
        string Credits() => CreditsWriter.Build(result.Changes, plugin => CreditsWriter.Origin(mo2, sources, plugin), result.AddedItems);

        AssetCopyResult? assets = null;
        // With merging off the look mods stay in the modlist, so there are no credits to write.
        string? creditsPath = null;
        if (forwardFolder != null)
        {
            // Assets and credits are written into a staging folder and swapped in at the end, so a failed copy keeps the previous output.
            var staging = fileSystem.Path.Combine(forwardFolder, ForwardFolder.StagingFolderName);
            bool wasMissing = false, wasEmpty = false, hadOutput = false;
            // A folder the patcher refuses (someone else's) is never rolled back: only what this run created or changed is.
            var claimed = false;
            var step = "checking the forward folder";
            try
            {
                wasMissing = !fileSystem.Directory.Exists(forwardFolder);
                wasEmpty = ForwardFolder.IsMissingOrEmpty(fileSystem, forwardFolder);
                // A leftover staging folder of a killed first run is no output, though it makes the folder non-empty.
                hadOutput = ForwardFolder.HasOutput(fileSystem, forwardFolder);
                // A leftover previous folder fails the run in BeginStaging before any change, so there is nothing to roll back either.
                claimed = !ForwardFolder.IsForeign(fileSystem, forwardFolder) && !ForwardFolder.HasLeftoverPrevious(fileSystem, forwardFolder);
                step = "preparing the staging folder";
                staging = ForwardFolder.BeginStaging(fileSystem, forwardFolder, log);
                step = "copying the assets";
                if (result.Assets.Count > 0)
                    assets = AssetCopier.Copy(result.Assets, mo2, forwardFolder, inputs.DataFolder, loadOrderKeys, fileSystem, log, targetFolder: staging, lookSources: sources);
                step = "writing CREDITS.md";
                fileSystem.File.WriteAllText(fileSystem.Path.Combine(staging, CreditsWriter.FileName), Credits());
                step = "swapping the new output in";
                // The commit point: it returns only once the new content is in place, and its cleanup afterwards never fails the run.
                ForwardFolder.CommitStaging(fileSystem, forwardFolder, staging, log);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var left = claimed ? ForwardFolder.Rollback(fileSystem, forwardFolder, staging, wasMissing, wasEmpty, log) : null;
                var state = left switch
                {
                    null => $"Nothing in the forward folder {forwardFolder} was changed.",
                    { Count: > 0 } => $"The forward folder {forwardFolder} is not as it was; left behind: {string.Join("; ", left)}."
                        + (wasEmpty ? " There was no previous output before this run." : string.Empty),
                    _ when wasMissing => $"There was no previous output: the forward folder {forwardFolder} did not exist and was removed again.",
                    _ when wasEmpty => $"There was no previous output: the forward folder {forwardFolder} was empty and is empty again.",
                    _ when !hadOutput => $"There was no previous output: the forward folder {forwardFolder} held none and holds none again.",
                    _ => $"The forward folder {forwardFolder} keeps its previous output.",
                };
                throw new RunFailedException(
                    $"The run failed while {step} ({ex.Message}); {state} "
                    + "Close any program that has files of the forward folder or the look mods open, and check the free disk space, then run the patcher again.",
                    ex);
            }
            catch (Exception ex)
            {
                if (claimed)
                    ForwardFolder.Rollback(
                        fileSystem, forwardFolder, staging, wasMissing, wasEmpty, log, previousNotFromThisRun: ex is RunFailedException { PreviousNotFromThisRun: true });
                throw;
            }
            creditsPath = fileSystem.Path.Combine(forwardFolder, CreditsWriter.FileName);
        }

        PrintSummary(result, assets, sources, creditsPath, options.AllowMerging, copyAddedItems, log);
        return result;
    }

    private static void PrintSummary(
        MergeResult result, AssetCopyResult? assets, IReadOnlyList<LookSource> sources, string? creditsPath, bool selfContained, bool copyAddedItems,
        CountingLog log)
    {
        var (warnings, errors) = (log.Warnings, log.Errors);
        log.Info("--- Summary ---");
        foreach (var source in sources)
        {
            // What the plugin supplied: the item looks it won, the added items whose copied version is its, the forms whose version is its.
            var perType = result.Changes
                .Where(change => change.LookSupplier == source.ModKey)
                .GroupBy(change => change.Signature)
                .Select(group => $"{group.Key} {group.Count()}")
                .ToList();
            var perAddedType = result.AddedItems
                .Where(item => (item.Supplier ?? item.Original.ModKey) == source.ModKey)
                .GroupBy(item => item.Signature)
                .Select(group => $"{group.Key} {group.Count()}")
                .ToList();
            var forms = result.Copies.Count(copy => copy.LookPlugin == source.ModKey);
            var parts = new List<string>();
            if (perType.Count > 0) parts.Add(string.Join(", ", perType));
            if (perAddedType.Count > 0) parts.Add($"added items copied: {string.Join(", ", perAddedType)}");
            if (forms > 0) parts.Add($"forms the looks depend on: {forms}");
            log.Info($"{source.ModKey}: {(parts.Count == 0 ? "no item changed" : string.Join("; ", parts))}");
        }
        log.Info($"Items changed: {result.Changes.Count}. Skipped with identical looks: {result.IdenticalSkipped}.");
        if (copyAddedItems)
        {
            var perAdded = result.AddedItems
                .GroupBy(item => item.Signature)
                .Select(group => $"{group.Key} {group.Count()}")
                .ToList();
            log.Info(
                result.AddedItems.Count == 0
                    ? "Added items copied into the patch: none."
                    : $"Added items copied into the patch: {result.AddedItems.Count} ({string.Join(", ", perAdded)}).");
        }
        var perCopiedType = result.Copies
            .GroupBy(copy => copy.Signature)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key} {group.Count()}")
            .ToList();
        log.Info(
            result.Copies.Count == 0
                ? "Forms the looks depend on copied into the patch: none."
                : $"Forms the looks depend on copied into the patch: {result.Copies.Count} ({string.Join(", ", perCopiedType)}); "
                    + $"added by look plugins: {result.DuplicatedRecords}, copies of shared records: {result.ForwardedLookRecords}.");
        if (result.TypeSkipped > 0) log.Info($"Items left unchanged because of a type discrepancy: {result.TypeSkipped}.");
        if (result.ArmorTypeKept > 0)
            log.Info($"Armor whose look has another armor type than the modlist's keeps the modlist's type: {result.ArmorTypeKept} (Verbose logging lists them).");
        log.Info(selfContained
            ? "The patch has no look plugin as a master: you can disable the look mods until the next run (enable them again before it)."
            : "Content merging is off: the patch keeps the look plugins as masters, so they must stay enabled.");
        if (assets != null)
            log.Info(
                $"Assets copied: {assets.Copied}. Vanilla assets skipped: {assets.BaseGameSkipped}. "
                + $"Assets from other mods (warned): {assets.FromOtherMods}. Missing assets (warned): {assets.Missing}.");
        if (creditsPath != null) log.Info($"Credits: {creditsPath}");
        log.Info($"Warnings: {warnings}. Errors: {errors}.");
    }

    /// <summary>Passes everything on and counts the warnings and errors for the summary.</summary>
    private sealed class CountingLog(ILog inner) : ILog
    {
        public int Warnings { get; private set; }

        public int Errors { get; private set; }

        public void Info(string text) => inner.Info(text);

        public void Warning(string text)
        {
            Warnings++;
            inner.Warning(text);
        }

        public void Error(string text)
        {
            Errors++;
            inner.Error(text);
        }

        public void Verbose(string text) => inner.Verbose(text);
    }
}
