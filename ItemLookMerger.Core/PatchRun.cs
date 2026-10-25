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
/// <param name="OutputPath">The patch file Synthesis writes; CREDITS.md goes next to it when merging is off.</param>
/// <param name="DataFolder">The game's Data folder as Synthesis sees it (under MO2, its virtual view of the enabled mods).</param>
public sealed record RunInputs(
    IReadOnlyList<ISkyrimModGetter> LoadOrder,
    IReadOnlyList<ModKey> EnabledPlugins,
    ISkyrimMod Patch,
    string OutputPath,
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
        // With merging on, the patch is made independent of the look plugins, so they can be disabled after the run.
        var result = LookMerger.Merge(inputs.LoadOrder, inputs.Patch, sources, options.ForwardNames, selfContained: options.AllowMerging, options.OnTypeDiscrepancy, log);
        string Credits() => CreditsWriter.Build(result.Changes, plugin => CreditsWriter.Origin(mo2, sources, plugin));

        AssetCopyResult? assets = null;
        string creditsPath;
        if (forwardFolder != null)
        {
            // Assets and credits are written into a staging folder and swapped in at the end, so a failed copy keeps the previous output.
            var staging = ForwardFolder.BeginStaging(fileSystem, forwardFolder, log);
            try
            {
                if (result.Assets.Count > 0)
                    assets = AssetCopier.Copy(result.Assets, mo2, forwardFolder, inputs.DataFolder, loadOrderKeys, fileSystem, log, targetFolder: staging);
                fileSystem.File.WriteAllText(fileSystem.Path.Combine(staging, CreditsWriter.FileName), Credits());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ForwardFolder.DiscardStaging(fileSystem, staging, log);
                throw new RunFailedException(
                    $"Writing the assets or CREDITS.md failed ({ex.Message}); the forward folder {forwardFolder} keeps its previous output. "
                    + "Close any program that has those files open, and check the free disk space, then run the patcher again.");
            }
            catch
            {
                ForwardFolder.DiscardStaging(fileSystem, staging, log);
                throw;
            }
            ForwardFolder.CommitStaging(fileSystem, forwardFolder, staging, log);
            creditsPath = fileSystem.Path.Combine(forwardFolder, CreditsWriter.FileName);
        }
        else
        {
            var creditsFolder = fileSystem.Path.GetDirectoryName(fileSystem.Path.GetFullPath(inputs.OutputPath))!;
            creditsPath = fileSystem.Path.Combine(creditsFolder, CreditsWriter.FileName);
            fileSystem.Directory.CreateDirectory(creditsFolder);
            fileSystem.File.WriteAllText(creditsPath, Credits());
        }

        PrintSummary(result, assets, sources, creditsPath, options.AllowMerging, log);
        return result;
    }

    private static void PrintSummary(
        MergeResult result, AssetCopyResult? assets, IReadOnlyList<LookSource> sources, string creditsPath, bool selfContained, CountingLog log)
    {
        var (warnings, errors) = (log.Warnings, log.Errors);
        log.Info("--- Summary ---");
        foreach (var source in sources)
        {
            var perType = result.Changes
                .Where(change => change.LookPlugins.Contains(source.ModKey))
                .GroupBy(change => change.Signature)
                .Select(group => $"{group.Key} {group.Count()}")
                .ToList();
            log.Info($"{source.ModKey}: {(perType.Count == 0 ? "no item changed" : string.Join(", ", perType))}");
        }
        log.Info($"Items changed: {result.Changes.Count}. Skipped with identical looks: {result.IdenticalSkipped}.");
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
        log.Info($"Credits: {creditsPath}");
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
