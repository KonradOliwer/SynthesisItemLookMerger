using System.IO.Abstractions.TestingHelpers;
using ItemLookMerger.Core;
using ItemLookMerger.Core.Assets;
using ItemLookMerger.Core.Mo2;
using ItemLookMerger.Tests.Support;

namespace ItemLookMerger.Tests.Assets;

public class ForwardFolderTests
{
    private const string Mods = @"C:\MO2Portable\mods";
    private static readonly string Folder = Path.Combine(Mods, "OUTPUT ItemLookMerger");
    private static readonly string Marker = Path.Combine(Folder, ForwardFolder.MarkerFileName);

    private readonly MockFileSystem _fileSystem = Mo2Fixtures.Portable();
    private readonly TestLog _log = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("..")]
    [InlineData(".")]
    // Windows drops trailing dots, so "..." is the mods folder itself.
    [InlineData("...")]
    [InlineData(@"..\Other Mod")]
    [InlineData("Sub/Folder")]
    [InlineData(@"Sub\Folder")]
    [InlineData(@"C:\Elsewhere")]
    [InlineData(@"\Rooted")]
    public void Names_that_could_leave_the_mods_folder_are_rejected(string? name)
    {
        var mo2 = Mo2Instance.Load(_fileSystem, Mo2Fixtures.PortableFolder);
        Assert.Throws<RunFailedException>(() => ForwardFolder.Resolve(_fileSystem, mo2, name));
    }

    [Fact]
    public void A_plain_name_resolves_directly_inside_the_mods_folder()
    {
        var mo2 = Mo2Instance.Load(_fileSystem, Mo2Fixtures.PortableFolder);
        Assert.Equal(Folder, ForwardFolder.Resolve(_fileSystem, mo2, " OUTPUT ItemLookMerger "));
    }

    [Theory]
    [InlineData("Armors... Remastered")]
    [InlineData("Foo..Bar SE")]
    [InlineData("..Leading dots")]
    public void Dots_inside_a_name_are_allowed(string name)
    {
        var mo2 = Mo2Instance.Load(_fileSystem, Mo2Fixtures.PortableFolder);
        Assert.Equal(Path.Combine(Mods, name), ForwardFolder.Resolve(_fileSystem, mo2, name));
    }

    [Fact]
    public void A_mods_folder_at_a_drive_root_is_accepted()
    {
        _fileSystem.AddFile(@"C:\Root\ModOrganizer.ini", new MockFileData("[General]\r\ngamePath=C:/Games/Skyrim\r\n[Settings]\r\nmod_directory=E:/\r\n"));
        _fileSystem.AddFile(@"C:\Root\profiles\Default\modlist.txt", new MockFileData("+A\r\n"));
        var mo2 = Mo2Instance.Load(_fileSystem, @"C:\Root");

        Assert.Equal(@"E:\OUTPUT ItemLookMerger", ForwardFolder.Resolve(_fileSystem, mo2, "OUTPUT ItemLookMerger"));
    }

    [Fact]
    public void A_link_inside_the_folder_is_removed_itself_without_following_it()
    {
        _fileSystem.AddFile(Marker, new MockFileData("marker"));
        var link = Path.Combine(Folder, "linked textures");
        _fileSystem.AddDirectory(link);
        _fileSystem.File.SetAttributes(link, FileAttributes.Directory | FileAttributes.ReparsePoint);

        ForwardFolder.CommitStaging(_fileSystem, Folder, ForwardFolder.BeginStaging(_fileSystem, Folder, _log), _log);

        Assert.False(_fileSystem.Directory.Exists(link));
        Assert.True(_fileSystem.File.Exists(Marker));
    }

    [Fact]
    public void A_new_folder_is_created_with_the_marker_and_an_empty_staging_folder()
    {
        var staging = ForwardFolder.BeginStaging(_fileSystem, Folder, _log);

        Assert.True(_fileSystem.File.Exists(Marker));
        Assert.Equal(Path.Combine(Folder, ForwardFolder.StagingFolderName), staging);
        Assert.Empty(_fileSystem.Directory.EnumerateFileSystemEntries(staging));

        ForwardFolder.CommitStaging(_fileSystem, Folder, staging, _log);

        Assert.Equal([Marker], _fileSystem.Directory.EnumerateFileSystemEntries(Folder).ToArray());
    }

    [Fact]
    public void A_commit_replaces_the_output_keeps_the_marker_and_touches_nothing_outside()
    {
        _fileSystem.AddFile(Marker, new MockFileData("marker"));
        _fileSystem.AddFile(Path.Combine(Folder, "CREDITS.md"), new MockFileData("old"));
        _fileSystem.AddFile(Path.Combine(Folder, @"Meshes\Armor\old.nif"), new MockFileData([1]));
        _fileSystem.AddFile(Path.Combine(Mods, "OUTPUT ItemLookMerger Backup", "keep.txt"), new MockFileData("keep"));
        _fileSystem.AddFile(Path.Combine(Mods, "Other Mod", "keep.nif"), new MockFileData([2]));

        var staging = ForwardFolder.BeginStaging(_fileSystem, Folder, _log);
        // Staging leaves the previous output alone until the commit.
        Assert.Equal("old", _fileSystem.File.ReadAllText(Path.Combine(Folder, "CREDITS.md")));
        _fileSystem.AddFile(Path.Combine(staging, "CREDITS.md"), new MockFileData("new"));
        _fileSystem.AddFile(Path.Combine(staging, @"Meshes\Armor\new.nif"), new MockFileData([3]));
        ForwardFolder.CommitStaging(_fileSystem, Folder, staging, _log);

        Assert.Equal(
            new[] { "CREDITS.md", "Meshes", ForwardFolder.MarkerFileName }.Order(StringComparer.OrdinalIgnoreCase),
            _fileSystem.Directory.EnumerateFileSystemEntries(Folder).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal("new", _fileSystem.File.ReadAllText(Path.Combine(Folder, "CREDITS.md")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(Folder, @"Meshes\Armor\new.nif")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(Folder, @"Meshes\Armor\old.nif")));
        Assert.Equal("marker", _fileSystem.File.ReadAllText(Marker));
        Assert.True(_fileSystem.File.Exists(Path.Combine(Mods, "OUTPUT ItemLookMerger Backup", "keep.txt")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(Mods, "Other Mod", "keep.nif")));
    }

    [Fact]
    public void A_discarded_staging_folder_leaves_the_previous_output()
    {
        _fileSystem.AddFile(Marker, new MockFileData("marker"));
        _fileSystem.AddFile(Path.Combine(Folder, "CREDITS.md"), new MockFileData("old"));

        var staging = ForwardFolder.BeginStaging(_fileSystem, Folder, _log);
        _fileSystem.AddFile(Path.Combine(staging, "CREDITS.md"), new MockFileData("new"));
        ForwardFolder.DiscardStaging(_fileSystem, staging, _log);

        Assert.False(_fileSystem.Directory.Exists(staging));
        Assert.Equal("old", _fileSystem.File.ReadAllText(Path.Combine(Folder, "CREDITS.md")));
    }

    [Fact]
    public void Leftovers_of_a_run_stopped_mid_swap_are_cleaned_up_and_the_older_output_put_back()
    {
        _fileSystem.AddFile(Marker, new MockFileData("marker"));
        _fileSystem.AddFile(Path.Combine(Folder, ForwardFolder.StagingFolderName, "half.nif"), new MockFileData([1]));
        _fileSystem.AddFile(Path.Combine(Folder, ForwardFolder.PreviousFolderName, "CREDITS.md"), new MockFileData("older"));
        _fileSystem.AddFile(Path.Combine(Folder, ForwardFolder.PreviousFolderName, "moved_in.nif"), new MockFileData([2]));
        _fileSystem.AddFile(Path.Combine(Folder, "moved_in.nif"), new MockFileData([3]));

        var staging = ForwardFolder.BeginStaging(_fileSystem, Folder, _log);

        Assert.Empty(_fileSystem.Directory.EnumerateFileSystemEntries(staging));
        Assert.False(_fileSystem.Directory.Exists(Path.Combine(Folder, ForwardFolder.PreviousFolderName)));
        Assert.Equal("older", _fileSystem.File.ReadAllText(Path.Combine(Folder, "CREDITS.md")));
        // An entry the stopped swap had already moved in stays; the older one of that name is dropped.
        Assert.Equal([3], _fileSystem.File.ReadAllBytes(Path.Combine(Folder, "moved_in.nif")));
        Assert.Contains(_log.Warnings, warning => warning.Contains("put back"));
    }

    [Fact]
    public void A_non_empty_folder_without_the_marker_fails_the_run_and_is_left_alone()
    {
        _fileSystem.AddFile(Path.Combine(Folder, "someone_elses.esp"), new MockFileData([3]));

        var ex = Assert.Throws<RunFailedException>(() => ForwardFolder.BeginStaging(_fileSystem, Folder, _log));

        Assert.Contains("another mod", ex.Message);
        Assert.Equal([Path.Combine(Folder, "someone_elses.esp")], _fileSystem.Directory.EnumerateFileSystemEntries(Folder).ToArray());
    }

    [Fact]
    public void An_empty_folder_without_the_marker_is_taken_over()
    {
        _fileSystem.AddDirectory(Folder);

        ForwardFolder.BeginStaging(_fileSystem, Folder, _log);

        Assert.True(_fileSystem.File.Exists(Marker));
    }
}
