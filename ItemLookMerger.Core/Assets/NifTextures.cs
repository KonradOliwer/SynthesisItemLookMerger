using NiflySharp;
using NiflySharp.Blocks;

namespace ItemLookMerger.Core.Assets;

/// <summary>The texture paths a NIF uses: its shader texture sets and the effect shader textures (read with NiflySharp).</summary>
internal static class NifTextures
{
    /// <summary>The data-relative texture paths ("Textures\..."), or an empty list when the NIF cannot be read.</summary>
    public static IReadOnlyList<string> Read(byte[] nifBytes)
    {
        var nif = new NifFile();
        try
        {
            using var stream = new MemoryStream(nifBytes, writable: false);
            if (nif.Load(stream) != 0 || nif.Blocks == null) return [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // NiflySharp can fail on a malformed NIF in many ways (casts, overflows, bad indices). A broken NIF is still copied;
            // only its textures stay unknown.
            return [];
        }
        var raw = new List<string?>();
        foreach (var block in nif.Blocks)
        {
            switch (block)
            {
                case BSShaderTextureSet textureSet:
                    raw.AddRange(textureSet.Textures?.Select(texture => texture?.Content) ?? []);
                    break;
                case BSEffectShaderProperty effect:
                    raw.AddRange([
                        effect.SourceTexture?.Content, effect.GreyscaleTexture?.Content, effect.EnvMapTexture?.Content,
                        effect.NormalTexture?.Content, effect.EnvMaskTexture?.Content, effect.ReflectanceTexture?.Content,
                        effect.LightingTexture?.Content, effect.EmitGradientTexture?.Content,
                    ]);
                    break;
            }
        }
        return raw.Select(Normalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// NIFs store texture paths loosely: forward slashes, a "Data\" prefix, sometimes without "Textures\" or with junk before it.
    /// Returns the path from "Textures\" on, or null for an empty slot.
    /// </summary>
    internal static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var cleaned = path.Trim().Replace('/', '\\').TrimStart('\\');
        var texturesAt = cleaned.IndexOf(@"textures\", StringComparison.OrdinalIgnoreCase);
        if (texturesAt == 0 || (texturesAt > 0 && cleaned[texturesAt - 1] == '\\')) return cleaned[texturesAt..];
        if (cleaned.StartsWith(@"data\", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[5..];
        return @"Textures\" + cleaned;
    }
}
