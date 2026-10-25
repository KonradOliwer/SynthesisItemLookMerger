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

        ForwardFolder.PrepareEmpty(_fileSystem, Folder, _log);

        Assert.False(_fileSystem.Directory.Exists(link));
        Assert.True(_fileSystem.File.Exists(Marker));
    }

    [Fact]
    public void A_new_folder_is_created_with_the_marker()
    {
        ForwardFolder.PrepareEmpty(_fileSystem, Folder, _log);

        Assert.True(_fileSystem.File.Exists(Marker));
        Assert.Single(_fileSystem.Directory.EnumerateFileSystemEntries(Folder));
    }

    [Fact]
    public void An_owned_folder_is_emptied_but_keeps_the_folder_and_marker_and_nothing_outside_is_touched()
    {
        _fileSystem.AddFile(Marker, new MockFileData("marker"));
        _fileSystem.AddFile(Path.Combine(Folder, "CREDITS.md"), new MockFileData("old"));
        _fileSystem.AddFile(Path.Combine(Folder, @"Meshes\Armor\old.nif"), new MockFileData([1]));
        _fileSystem.AddFile(Path.Combine(Mods, "OUTPUT ItemLookMerger Backup", "keep.txt"), new MockFileData("keep"));
        _fileSystem.AddFile(Path.Combine(Mods, "Other Mod", "keep.nif"), new MockFileData([2]));

        ForwardFolder.PrepareEmpty(_fileSystem, Folder, _log);

        Assert.Equal([Marker], _fileSystem.Directory.EnumerateFileSystemEntries(Folder).ToArray());
        Assert.Equal("marker", _fileSystem.File.ReadAllText(Marker));
        Assert.True(_fileSystem.File.Exists(Path.Combine(Mods, "OUTPUT ItemLookMerger Backup", "keep.txt")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(Mods, "Other Mod", "keep.nif")));
    }

    [Fact]
    public void A_non_empty_folder_without_the_marker_fails_the_run_and_is_left_alone()
    {
        _fileSystem.AddFile(Path.Combine(Folder, "someone_elses.esp"), new MockFileData([3]));

        var ex = Assert.Throws<RunFailedException>(() => ForwardFolder.PrepareEmpty(_fileSystem, Folder, _log));

        Assert.Contains("another mod", ex.Message);
        Assert.True(_fileSystem.File.Exists(Path.Combine(Folder, "someone_elses.esp")));
        Assert.False(_fileSystem.File.Exists(Marker));
    }

    [Fact]
    public void An_empty_folder_without_the_marker_is_taken_over()
    {
        _fileSystem.AddDirectory(Folder);

        ForwardFolder.PrepareEmpty(_fileSystem, Folder, _log);

        Assert.True(_fileSystem.File.Exists(Marker));
    }
}
