namespace ItemLookMerger.Core.Mo2;

/// <summary>The download facts of a mod from the [General] section of its meta.ini. Missing values are empty.</summary>
public sealed record ModMeta(string ModId, string Version, string InstallationFile, string Repository, string Url)
{
    private const string NexusModsUrl = "https://www.nexusmods.com/skyrimspecialedition/mods/";

    /// <summary>The mod's Nexus page, when it came from Nexus with a real mod id.</summary>
    public string? NexusUrl =>
        string.Equals(Repository, "Nexus", StringComparison.OrdinalIgnoreCase) && int.TryParse(ModId, out var id) && id > 0
            ? NexusModsUrl + id
            : null;

    internal static ModMeta Parse(string text)
    {
        var ini = IniDocument.Parse(text);
        string Value(string key) => ini.Get("General", key) ?? string.Empty;
        return new ModMeta(Value("modid"), Value("version"), Value("installationFile"), Value("repository"), Value("url"));
    }
}
