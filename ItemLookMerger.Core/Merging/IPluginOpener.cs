using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Core.Merging;

/// <summary>Opens a plugin outside the load order from its file. A returned plugin that is IDisposable is disposed at the end of the run.</summary>
public interface IPluginOpener
{
    ISkyrimModGetter Open(string path);
}

/// <summary>Reads the plugin file lazily (binary overlay), as Synthesis does for the load order.</summary>
public sealed class OverlayPluginOpener : IPluginOpener
{
    public ISkyrimModGetter Open(string path) =>
        SkyrimMod.CreateFromBinaryOverlay(new Mutagen.Bethesda.Plugins.ModPath(path), SkyrimRelease.SkyrimSE);
}
