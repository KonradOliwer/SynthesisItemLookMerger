using System.IO.Abstractions;
using System.Xml;
using System.Xml.Linq;

namespace ItemLookMerger.Core.Assets;

/// <param name="Project">The .osp file, relative to the mod folder.</param>
/// <param name="DataFolder">The folder under ShapeData that holds the set's source NIFs, or null when the set names none.</param>
/// <param name="Outputs">The data-relative meshes a BodySlide build of the set writes.</param>
internal sealed record BodySlideSet(string Project, string Name, string? DataFolder, IReadOnlyList<string> Outputs);

/// <param name="Path">The group file, relative to the mod folder.</param>
internal sealed record BodySlideGroup(string Path, IReadOnlySet<string> Members);

/// <summary>
/// The BodySlide projects among a look mod's loose files (BSAs do not hold them): the slider sets of CalienteTools\BodySlide\SliderSets\*.osp
/// with the meshes they build, and the SliderGroups that list sets. A file that is not valid XML is warned about and skipped.
/// </summary>
internal sealed class BodySlideProjects
{
    private const string Root = @"CalienteTools\BodySlide";

    private readonly IFileSystem _fileSystem;
    private readonly string _modFolder;
    private readonly string _modName;
    private readonly ILog _log;

    private BodySlideProjects(
        IFileSystem fileSystem, string modFolder, string modName, ILog log, IReadOnlyList<BodySlideSet> sets, IReadOnlyList<BodySlideGroup> groups)
    {
        _fileSystem = fileSystem;
        _modFolder = modFolder;
        _modName = modName;
        _log = log;
        Sets = sets;
        Groups = groups;
    }

    public IReadOnlyList<BodySlideSet> Sets { get; }

    public IReadOnlyList<BodySlideGroup> Groups { get; }

    public static BodySlideProjects Read(IFileSystem fileSystem, string modFolder, string modName, ILog log)
    {
        var sets = new List<BodySlideSet>();
        foreach (var file in Files(fileSystem, fileSystem.Path.Combine(modFolder, Root, "SliderSets"), "*.osp", SearchOption.AllDirectories, modName, log))
        {
            var project = fileSystem.Path.GetRelativePath(modFolder, file);
            if (Load(fileSystem, file, project, modName, log) is not { } document) continue;
            foreach (var set in document.Descendants("SliderSet"))
            {
                var name = (string?)set.Attribute("name");
                var outputPath = set.Element("OutputPath")?.Value.Trim();
                var outputFile = set.Element("OutputFile");
                var fileName = outputFile?.Value.Trim();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(outputPath) || string.IsNullOrEmpty(fileName)) continue;
                if (fileName.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) fileName = fileName[..^4];
                var stem = AssetPath.Key(outputPath).TrimEnd('\\') + @"\" + fileName;
                var genWeights = string.Equals((string?)outputFile!.Attribute("GenWeights"), "true", StringComparison.OrdinalIgnoreCase);
                var dataFolder = set.Element("DataFolder")?.Value.Trim();
                sets.Add(new BodySlideSet(
                    project, name, string.IsNullOrEmpty(dataFolder) ? null : dataFolder,
                    genWeights ? [stem + "_0.nif", stem + "_1.nif"] : [stem + ".nif"]));
            }
        }

        var groups = new List<BodySlideGroup>();
        foreach (var file in Files(fileSystem, fileSystem.Path.Combine(modFolder, Root, "SliderGroups"), "*.xml", SearchOption.AllDirectories, modName, log))
        {
            var path = fileSystem.Path.GetRelativePath(modFolder, file);
            if (Load(fileSystem, file, path, modName, log) is not { } document) continue;
            var members = document.Descendants("Member").Select(member => (string?)member.Attribute("name")).OfType<string>();
            groups.Add(new BodySlideGroup(path, members.ToHashSet(StringComparer.OrdinalIgnoreCase)));
        }
        return new BodySlideProjects(fileSystem, fileSystem.Path.GetFullPath(modFolder), modName, log, sets, groups);
    }

    /// <summary>The slider sets that build <paramref name="meshPath"/>.</summary>
    public IReadOnlyList<BodySlideSet> Producing(string meshPath)
    {
        var key = AssetPath.Key(meshPath);
        return Sets.Where(set => set.Outputs.Any(output => string.Equals(output, key, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Every loose file under the set's ShapeData folder, relative to the mod folder.</summary>
    public IReadOnlyList<string> ShapeDataFiles(string dataFolder)
    {
        var folder = AssetPath.Inside(_fileSystem, _modFolder, $@"{Root}\ShapeData\{dataFolder}");
        if (folder == null) return [];
        return Files(_fileSystem, folder, "*", SearchOption.AllDirectories, _modName, _log)
            .Select(file => _fileSystem.Path.GetRelativePath(_modFolder, file))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The files of the folder, ordered; a folder that cannot be listed is warned about and counts as empty.</summary>
    private static IReadOnlyList<string> Files(IFileSystem fileSystem, string folder, string pattern, SearchOption option, string modName, ILog log)
    {
        try
        {
            return fileSystem.Directory.Exists(folder)
                ? fileSystem.Directory.EnumerateFiles(folder, pattern, option).Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"BodySlide folder {folder} of MO2 mod \"{modName}\" could not be listed ({ex.Message.TrimEnd()}); it is skipped.");
            return [];
        }
    }

    private static XDocument? Load(IFileSystem fileSystem, string file, string relative, string modName, ILog log)
    {
        try
        {
            using var stream = fileSystem.File.OpenRead(file);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            return XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            log.Warning($"BodySlide file {relative} of MO2 mod \"{modName}\" could not be read ({ex.Message.TrimEnd()}); it is skipped.");
            return null;
        }
    }
}
