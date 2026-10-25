using System.IO.Abstractions;
using ItemLookMerger.Core.Assets;
using ItemLookMerger.Core.Credits;
using ItemLookMerger.Core.Merging;
using ItemLookMerger.Core.Mo2;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Core;

/// <summary>Everything one run works on; the entry project fills it from the Synthesis state.</summary>
/// <param name="LoadOrder">The enabled plugins of the load order without the patch, lowest priority first (read through the Data folder).</param>
/// <param name="OutputPath">The patch file Synthesis writes; CREDITS.md goes next to it when merging is off.</param>
/// <param name="DataFolder">The game's Data folder as Synthesis sees it (under MO2, its virtual view of the enabled mods).</param>
public sealed record RunInputs(
    IReadOnlyList<ISkyrimModGetter> LoadOrder,
    ISkyrimMod Patch,
    string OutputPath,
    string DataFolder,
    MergeOptions Options,
    IMo2Environment Mo2Environment,
    IFileSystem FileSystem,
    IPluginOpener PluginOpener,
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
        var mo2 = Mo2Locator.Locate(inputs.Mo2Environment, fileSystem, new LoadOrderFacts(inputs.DataFolder, loadOrderKeys), log);
        // The name is checked now; the folder itself is only emptied once the merge has succeeded, so a failed run leaves it alone.
        string? forwardFolder = null;
        if (options.AllowMerging) forwardFolder = ForwardFolder.Resolve(fileSystem, mo2, options.ForwardFolderName);
        else ForwardFolder.ReportLeftover(fileSystem, mo2, options.ForwardFolderName, log);

        IReadOnlyList<LookSource> sources = [];
        try
        {
            sources = SourceResolver.Resolve(
                options.ModsToForward, inputs.LoadOrder, inputs.Patch.ModKey, mo2, inputs.PluginOpener, options.AllowMerging,
                options.ForwardFolderName, fileSystem, log);
            if (sources.Count == 0)
            {
                // Nothing to forward: an earlier run's forward folder and CREDITS.md still describe its patch, so they stay as they are.
                log.Warning("\"Mods to forward\" lists no mod; nothing to forward. The forward folder and CREDITS.md are left as they are.");
                return new MergeResult([], 0, 0, 0, []);
            }
            var result = LookMerger.Merge(inputs.LoadOrder, inputs.Patch, sources, options.ForwardNames, log);

            AssetCopyResult? assets = null;
            if (forwardFolder != null)
            {
                ForwardFolder.PrepareEmpty(fileSystem, forwardFolder, log);
                if (result.Assets.Count > 0)
                    assets = AssetCopier.Copy(result.Assets, mo2, forwardFolder, inputs.DataFolder, loadOrderKeys, fileSystem, log);
            }

            var creditsFolder = forwardFolder ?? fileSystem.Path.GetDirectoryName(fileSystem.Path.GetFullPath(inputs.OutputPath))!;
            var creditsPath = fileSystem.Path.Combine(creditsFolder, CreditsWriter.FileName);
            fileSystem.Directory.CreateDirectory(creditsFolder);
            fileSystem.File.WriteAllText(creditsPath, CreditsWriter.Build(result.Changes, plugin => CreditsWriter.Origin(mo2, sources, plugin)));

            PrintSummary(result, assets, sources, creditsPath, log);
            return result;
        }
        finally
        {
            foreach (var source in sources.Where(source => !source.InLoadOrder)) (source.Mod as IDisposable)?.Dispose();
        }
    }

    private static void PrintSummary(MergeResult result, AssetCopyResult? assets, IReadOnlyList<LookSource> sources, string creditsPath, CountingLog log)
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
            log.Info($"{source.ModKey}{(source.InLoadOrder ? "" : " (merged in)")}: {(perType.Count == 0 ? "no item changed" : string.Join(", ", perType))}");
        }
        log.Info(
            $"Items changed: {result.Changes.Count}. Skipped with identical looks: {result.IdenticalSkipped}. "
            + $"Look records (ARMA, TXST, STAT, PROJ) forwarded: {result.ForwardedLookRecords}. Records copied from merged-in plugins: {result.DuplicatedRecords}.");
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
