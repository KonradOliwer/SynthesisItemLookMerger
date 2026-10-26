using NiflySharp;
using NiflySharp.Blocks;

namespace ItemLookMerger.Core.Assets;

/// <summary>
/// The other files a NIF points at (read with NiflySharp): its shader texture sets and effect shader textures, the SMP physics
/// config of its "HDT Skinned Mesh Physics Object" string extra data and the morph file of its "BODYTRI" one.
/// </summary>
internal static class NifReferences
{
    private const string SmpExtraData = "HDT Skinned Mesh Physics Object";
    private const string TriExtraData = "BODYTRI";

    /// <summary>The data-relative paths the NIF uses, or an empty list when the NIF cannot be read.</summary>
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
            // only its references stay unknown.
            return [];
        }
        var raw = new List<string?>();
        foreach (var block in nif.Blocks)
        {
            switch (block)
            {
                case BSShaderTextureSet textureSet:
                    raw.AddRange(textureSet.Textures?.Select(texture => NormalizeTexture(texture?.Content)) ?? []);
                    break;
                case BSEffectShaderProperty effect:
                    string?[] effectTextures = [
                        effect.SourceTexture?.Content, effect.GreyscaleTexture?.Content, effect.EnvMapTexture?.Content,
                        effect.NormalTexture?.Content, effect.EnvMaskTexture?.Content, effect.ReflectanceTexture?.Content,
                        effect.LightingTexture?.Content, effect.EmitGradientTexture?.Content,
                    ];
                    raw.AddRange(effectTextures.Select(NormalizeTexture));
                    break;
                case NiStringExtraData extra when string.Equals(extra.Name?.String?.Trim(), SmpExtraData, StringComparison.OrdinalIgnoreCase):
                    raw.Add(NormalizeData(extra.StringData?.String));
                    break;
                case NiStringExtraData extra when string.Equals(extra.Name?.String?.Trim(), TriExtraData, StringComparison.OrdinalIgnoreCase):
                    raw.Add(NormalizeUnder(extra.StringData?.String, "meshes"));
                    break;
            }
        }
        return raw.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// NIFs store texture paths loosely: forward slashes, a "Data\" prefix (or a full path to the Data folder), sometimes without "Textures\".
    /// Returns the path from "Textures\" on, or null for an empty slot.
    /// </summary>
    internal static string? NormalizeTexture(string? path) => NormalizeUnder(path, "Textures");

    /// <summary>A path the NIF gives relative to Data (an SMP config): backslashes, no "Data\" prefix; null when empty.</summary>
    internal static string? NormalizeData(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var cleaned = path.Trim().Replace('/', '\\').TrimStart('\\');
        if (cleaned.StartsWith(@"data\", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[5..];
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>A path the NIF gives relative to the <paramref name="root"/> folder, made data-relative; null when empty.</summary>
    internal static string? NormalizeUnder(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var cleaned = path.Trim().Replace('/', '\\').TrimStart('\\');
        // A full path (drive letter) is cut after its last "\Data\" folder; inner folder names are otherwise never cut at.
        if (cleaned.Length > 1 && cleaned[1] == ':' && cleaned.LastIndexOf(@"\data\", StringComparison.OrdinalIgnoreCase) is >= 0 and var dataAt)
            cleaned = cleaned[(dataAt + 6)..];
        if (cleaned.StartsWith(@"data\", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[5..];
        var prefix = root + @"\";
        return cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? cleaned : prefix + cleaned;
    }
}
