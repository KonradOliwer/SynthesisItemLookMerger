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
    public void A_link_like_entry_in_the_old_output_is_replaced_with_the_swap()
    {
        // The mock file system has no reparse semantics (only an attribute on an empty folder), so this does not prove that a link's
        // target is left alone; it covers the swap and the non-recursive removal path for such an entry.
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

    private static readonly string Previous = Path.Combine(Folder, ForwardFolder.PreviousFolderName);
    private static readonly string Staging = Path.Combine(Folder, ForwardFolder.StagingFolderName);

    /// <summary>An owned forward folder with an old output (a top-level file and a nested file) and a staged new output.</summary>
    private string ArrangeSwap()
    {
        _fileSystem.AddFile(Marker, new MockFileData("marker"));
        _fileSystem.AddFile(Path.Combine(Folder, "CREDITS.md"), new MockFileData("old"));
        _fileSystem.AddFile(Path.Combine(Folder, @"Meshes\Old\old.nif"), new MockFileData([1]));
        var staging = ForwardFolder.BeginStaging(_fileSystem, Folder, _log);
        _fileSystem.AddFile(Path.Combine(staging, "CREDITS.md"), new MockFileData("new"));
        _fileSystem.AddFile(Path.Combine(staging, @"Textures\new.dds"), new MockFileData([2]));
        return staging;
    }

    private static bool IsStagedEntry(string source, string name) =>
        source.Contains(ForwardFolder.StagingFolderName, StringComparison.Ordinal) && source.EndsWith(name, StringComparison.Ordinal);

    /// <summary>
    /// Fails the move of the staged Textures folder. The staged entries are moved in sorted order, so CREDITS.md is already in the
    /// forward folder when this fails: the swap-back has a moved-in entry to remove.
    /// </summary>
    private static Exception? FailStagedTextures(string source, string _) => IsStagedEntry(source, "Textures") ? new IOException("locked") : null;

    private void AssertOldOutputIntact()
    {
        Assert.Equal("old", _fileSystem.File.ReadAllText(Path.Combine(Folder, "CREDITS.md")));
        Assert.Equal([1], _fileSystem.File.ReadAllBytes(Path.Combine(Folder, @"Meshes\Old\old.nif")));
        Assert.False(_fileSystem.Directory.Exists(Path.Combine(Folder, "Textures")));
        Assert.True(_fileSystem.File.Exists(Marker));
    }

    [Fact]
    public void A_leftover_staging_folder_is_removed_and_the_output_stays()
    {
        _fileSystem.AddFile(Marker, new MockFileData("marker"));
        _fileSystem.AddFile(Path.Combine(Folder, "CREDITS.md"), new MockFileData("old"));
        _fileSystem.AddFile(Path.Combine(Staging, @"Meshes\half.nif"), new MockFileData([9]));

        var staging = ForwardFolder.BeginStaging(_fileSystem, Folder, _log);

        Assert.Equal(Staging, staging);
        Assert.Empty(_fileSystem.Directory.EnumerateFileSystemEntries(staging));
        Assert.Equal("old", _fileSystem.File.ReadAllText(Path.Combine(Folder, "CREDITS.md")));
    }

    /// <summary>The recovery text must name deleting the previous folder and rerunning, and never offer moving it back.</summary>
    private static void AssertDeleteAndRerunAdvice(string message)
    {
        Assert.Contains($"Delete {Previous} and run the patcher again", message, StringComparison.Ordinal);
        Assert.Contains("incomplete", message);
        Assert.DoesNotContain("delete everything else", message);
        Assert.DoesNotContain("move the contents", message);
        Assert.DoesNotContain("To go back", message);
    }

    [Fact]
    public void A_partly_deleted_previous_folder_fails_the_start_before_any_change_and_advises_deleting_it()
    {
        _fileSystem.AddFile(Marker, new MockFileData("marker"));
        _fileSystem.AddFile(Path.Combine(Folder, "CREDITS.md"), new MockFileData("current"));
        // A cleanup that stopped half-way: only one of the old entries is still in previous.
        _fileSystem.AddFile(Path.Combine(Previous, @"Meshes\Old\old.nif"), new MockFileData([1]));

        var ex = Assert.Throws<RunFailedException>(() => ForwardFolder.BeginStaging(_fileSystem, Folder, _log));

        AssertDeleteAndRerunAdvice(ex.Message);
        Assert.Contains("Nothing was changed", ex.Message);
        Assert.True(_fileSystem.File.Exists(Path.Combine(Previous, @"Meshes\Old\old.nif")));
        Assert.Equal("current", _fileSystem.File.ReadAllText(Path.Combine(Folder, "CREDITS.md")));
        Assert.False(_fileSystem.Directory.Exists(Staging));
    }

    [Fact]
    public void A_failed_move_of_an_old_entry_aside_restores_the_folder_without_data_loss()
    {
        var staging = ArrangeSwap();
        // The old entries are moved aside in sorted order: CREDITS.md is already in previous when the move of Meshes fails.
        var movedAside = new List<string>();
        var faulty = new FaultyFileSystem(_fileSystem)
        {
            OnMove = (source, target) =>
            {
                if (source == Path.Combine(Folder, "Meshes")) return new IOException("locked");
                if (target.StartsWith(Previous, StringComparison.Ordinal)) movedAside.Add(Path.GetFileName(target));
                return null;
            },
        };

        var ex = Assert.Throws<RunFailedException>(() => ForwardFolder.CommitStaging(faulty, Folder, staging, _log));

        Assert.Equal(["CREDITS.md"], movedAside);
        Assert.Contains("back as it was", ex.Message);
        AssertOldOutputIntact();
        Assert.False(_fileSystem.Directory.Exists(Previous));
        Assert.True(_fileSystem.File.Exists(Path.Combine(staging, @"Textures\new.dds")));
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Rollback_reports_a_previous_folder_it_did_not_make_as_untouched()
    {
        _fileSystem.AddFile(Marker, new MockFileData("marker"));
        _fileSystem.AddFile(Path.Combine(Folder, "CREDITS.md"), new MockFileData("old"));
        var staging = ForwardFolder.BeginStaging(_fileSystem, Folder, _log);
        _fileSystem.AddFile(Path.Combine(Previous, "stranger.txt"), new MockFileData("theirs"));

        var left = ForwardFolder.Rollback(_fileSystem, Folder, staging, wasMissing: false, wasEmpty: false, _log, previousNotFromThisRun: true);

        Assert.Empty(left);
        Assert.Contains(_log.Infos, line => line.Contains("left unchanged"));
        Assert.Empty(_log.Warnings);
        Assert.True(_fileSystem.File.Exists(Path.Combine(Previous, "stranger.txt")));
        Assert.False(_fileSystem.Directory.Exists(staging));
    }

    [Fact]
    public void HasOutput_ignores_the_marker_meta_ini_and_a_leftover_staging_folder()
    {
        _fileSystem.AddFile(Marker, new MockFileData("marker"));
        _fileSystem.AddFile(Path.Combine(Folder, ForwardFolder.MetaFileName), new MockFileData("notes"));
        _fileSystem.AddFile(Path.Combine(Staging, "half.nif"), new MockFileData([9]));
        Assert.False(ForwardFolder.HasOutput(_fileSystem, Folder));

        _fileSystem.AddFile(Path.Combine(Folder, "CREDITS.md"), new MockFileData("old"));
        Assert.True(ForwardFolder.HasOutput(_fileSystem, Folder));
    }

    [Fact]
    public void A_leftover_previous_folder_fails_the_start_before_any_change_and_is_left_untouched()
    {
        _fileSystem.AddFile(Marker, new MockFileData("marker"));
        _fileSystem.AddFile(Path.Combine(Folder, "CREDITS.md"), new MockFileData("current"));
        _fileSystem.AddFile(Path.Combine(Previous, "CREDITS.md"), new MockFileData("older"));
        _fileSystem.AddFile(Path.Combine(Staging, "half.nif"), new MockFileData([9]));

        var ex = Assert.Throws<RunFailedException>(() => ForwardFolder.BeginStaging(_fileSystem, Folder, _log));

        Assert.Contains(Previous, ex.Message, StringComparison.Ordinal);
        AssertDeleteAndRerunAdvice(ex.Message);
        Assert.Equal("older", _fileSystem.File.ReadAllText(Path.Combine(Previous, "CREDITS.md")));
        Assert.Equal("current", _fileSystem.File.ReadAllText(Path.Combine(Folder, "CREDITS.md")));
        // Not even the leftover staging folder is touched: the failure comes before any change.
        Assert.True(_fileSystem.File.Exists(Path.Combine(Staging, "half.nif")));
    }

    [Fact]
    public void A_failed_swap_removes_the_new_entries_puts_the_old_ones_back_and_deletes_the_previous_folder()
    {
        var staging = ArrangeSwap();
        var failure = new IOException("locked");
        var movedIn = new List<string>();
        var faulty = new FaultyFileSystem(_fileSystem)
        {
            OnMove = (source, target) =>
            {
                if (FailStagedTextures(source, target) is not null) return failure;
                if (source.Contains(ForwardFolder.StagingFolderName, StringComparison.Ordinal)) movedIn.Add(target);
                return null;
            },
        };

        var ex = Assert.Throws<RunFailedException>(() => ForwardFolder.CommitStaging(faulty, Folder, staging, _log));

        Assert.Same(failure, ex.InnerException);
        // The fault hit after the staged CREDITS.md had been moved in; it is removed again and the old content is fully back.
        Assert.Equal([Path.Combine(Folder, "CREDITS.md")], movedIn);
        AssertOldOutputIntact();
        Assert.True(_fileSystem.File.Exists(Path.Combine(staging, @"Textures\new.dds")));
        Assert.False(_fileSystem.Directory.Exists(Previous));
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void A_failed_restore_of_a_nested_file_keeps_the_previous_folder_logs_an_error_and_fails()
    {
        var staging = ArrangeSwap();
        var faulty = new FaultyFileSystem(_fileSystem)
        {
            OnMove = FailStagedTextures,
            // The old nested file is only locked once it has been moved aside.
            IsLocked = file => file.Contains(ForwardFolder.PreviousFolderName, StringComparison.Ordinal) && file.EndsWith("old.nif", StringComparison.Ordinal),
        };

        var ex = Assert.Throws<RunFailedException>(() => ForwardFolder.CommitStaging(faulty, Folder, staging, _log));

        Assert.IsType<IOException>(ex.InnerException);
        Assert.Contains(Previous, ex.Message, StringComparison.Ordinal);
        Assert.True(_fileSystem.File.Exists(Path.Combine(Previous, @"Meshes\Old\old.nif")));
        var error = Assert.Single(_log.Errors);
        Assert.Contains(Previous, error, StringComparison.Ordinal);
        Assert.DoesNotContain("move the contents", error);
        Assert.Contains($"delete {Previous}, and run the patcher again", error);
        Assert.Contains("incomplete", error);
        Assert.Contains("Put back already: CREDITS.md", error);
        // The staged CREDITS.md that got in was removed again and the entry that could be restored is back.
        Assert.False(_fileSystem.Directory.Exists(Path.Combine(Folder, "Textures")));
        Assert.Equal("old", _fileSystem.File.ReadAllText(Path.Combine(Folder, "CREDITS.md")));
    }

    [Fact]
    public void A_failed_cleanup_after_the_swap_only_warns_and_names_the_leftover()
    {
        var staging = ArrangeSwap();
        var faulty = new FaultyFileSystem(_fileSystem)
        {
            IsLocked = file => file.Contains(ForwardFolder.PreviousFolderName, StringComparison.Ordinal) && file.EndsWith("old.nif", StringComparison.Ordinal),
        };

        ForwardFolder.CommitStaging(faulty, Folder, staging, _log);

        Assert.Equal("new", _fileSystem.File.ReadAllText(Path.Combine(Folder, "CREDITS.md")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(Folder, @"Textures\new.dds")));
        Assert.False(_fileSystem.Directory.Exists(Path.Combine(Folder, "Meshes")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(Previous, @"Meshes\Old\old.nif")));
        Assert.Contains(_log.Warnings, warning => warning.Contains(Previous, StringComparison.Ordinal));
        Assert.Empty(_log.Errors);
        // The verbose line does not claim that the previous entries were removed.
        var verbose = Assert.Single(_log.Verboses, line => line.StartsWith("Replaced the forward folder's output", StringComparison.Ordinal));
        Assert.Contains("not cleaned up: " + Previous, verbose, StringComparison.Ordinal);
    }

    [Fact]
    public void A_staging_folder_that_cannot_be_removed_after_the_swap_only_warns_and_names_it()
    {
        var staging = ArrangeSwap();
        var faulty = new FaultyFileSystem(_fileSystem) { OnDelete = path => path == Staging ? new IOException("locked") : null };

        ForwardFolder.CommitStaging(faulty, Folder, staging, _log);

        Assert.Equal("new", _fileSystem.File.ReadAllText(Path.Combine(Folder, "CREDITS.md")));
        Assert.False(_fileSystem.Directory.Exists(Previous));
        Assert.Contains(_log.Warnings, warning => warning.Contains(Staging, StringComparison.Ordinal));
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Rollback_keeps_the_marker_while_the_staging_folder_could_not_be_removed()
    {
        _fileSystem.AddDirectory(Folder);
        var staging = ForwardFolder.BeginStaging(_fileSystem, Folder, _log);
        _fileSystem.AddFile(Path.Combine(staging, "half.nif"), new MockFileData([1]));
        var faulty = new FaultyFileSystem(_fileSystem) { IsLocked = file => file.EndsWith("half.nif", StringComparison.Ordinal) };

        var left = ForwardFolder.Rollback(faulty, Folder, staging, wasMissing: false, wasEmpty: true, _log);

        Assert.True(_fileSystem.File.Exists(Marker));
        Assert.True(_fileSystem.Directory.Exists(staging));
        Assert.Contains(left, item => item.Contains(staging, StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Infos, line => line.Contains("left unchanged"));
        // Nothing of ours is left to strand the folder: the next run still recognises it and removes the staging folder.
        Assert.False(ForwardFolder.IsForeign(_fileSystem, Folder));
    }

    [Fact]
    public void Rollback_keeps_the_marker_while_the_previous_folder_is_kept()
    {
        _fileSystem.AddDirectory(Folder);
        var staging = ForwardFolder.BeginStaging(_fileSystem, Folder, _log);
        _fileSystem.AddFile(Path.Combine(Previous, "older.nif"), new MockFileData([1]));

        var left = ForwardFolder.Rollback(_fileSystem, Folder, staging, wasMissing: false, wasEmpty: true, _log);

        Assert.True(_fileSystem.File.Exists(Marker));
        Assert.True(_fileSystem.File.Exists(Path.Combine(Previous, "older.nif")));
        Assert.Contains(left, item => item.Contains(Previous, StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Infos, line => line.Contains("left unchanged"));
    }

    [Fact]
    public void Rollback_leaves_an_existing_empty_folder_empty_and_a_missing_one_missing()
    {
        _fileSystem.AddDirectory(Folder);
        var staging = ForwardFolder.BeginStaging(_fileSystem, Folder, _log);

        ForwardFolder.Rollback(_fileSystem, Folder, staging, wasMissing: false, wasEmpty: true, _log);

        Assert.True(_fileSystem.Directory.Exists(Folder));
        Assert.Empty(_fileSystem.Directory.EnumerateFileSystemEntries(Folder));
        Assert.Contains(_log.Infos, line => line.Contains("left unchanged"));
    }

    [Fact]
    public void Rollback_removes_a_folder_the_run_created_with_its_marker_and_staging_folder()
    {
        // The folder is missing, so BeginStaging creates it with the marker and the staging folder; the rollback must remove all of it.
        var staging = ForwardFolder.BeginStaging(_fileSystem, Folder, _log);
        Assert.True(_fileSystem.Directory.Exists(Folder));

        var left = ForwardFolder.Rollback(_fileSystem, Folder, staging, wasMissing: true, wasEmpty: true, _log);

        Assert.Empty(left);
        Assert.False(_fileSystem.Directory.Exists(Folder));
    }

    [Fact]
    public void Rollback_keeps_meta_ini_in_a_folder_it_resets()
    {
        var meta = Path.Combine(Folder, ForwardFolder.MetaFileName);
        _fileSystem.AddFile(meta, new MockFileData("[General]"));
        var staging = ForwardFolder.BeginStaging(_fileSystem, Folder, _log);

        ForwardFolder.Rollback(_fileSystem, Folder, staging, wasMissing: false, wasEmpty: true, _log);

        Assert.Equal("[General]", _fileSystem.File.ReadAllText(meta));
        Assert.False(_fileSystem.File.Exists(Marker));
        Assert.False(_fileSystem.Directory.Exists(staging));
    }

    [Fact]
    public void Rollback_does_not_claim_an_unchanged_folder_when_it_could_not_clean_up()
    {
        _fileSystem.AddFile(Marker, new MockFileData("marker"));
        var staging = ForwardFolder.BeginStaging(_fileSystem, Folder, _log);
        _fileSystem.AddFile(Path.Combine(staging, "half.nif"), new MockFileData([1]));
        var faulty = new FaultyFileSystem(_fileSystem) { OnDelete = _ => new InvalidOperationException("odd") };

        ForwardFolder.Rollback(faulty, Folder, staging, wasMissing: false, wasEmpty: false, _log);

        Assert.DoesNotContain(_log.Infos, line => line.Contains("left unchanged"));
        Assert.Contains(_log.Warnings, warning => warning.Contains(staging, StringComparison.Ordinal));
    }

    [Fact]
    public void Rollback_never_throws_when_even_checking_the_folder_fails()
    {
        _fileSystem.AddFile(Marker, new MockFileData("marker"));
        var staging = ForwardFolder.BeginStaging(_fileSystem, Folder, _log);
        // The check of the folder itself sits outside the inner try blocks, so this reaches the outer catch.
        var faulty = new FaultyFileSystem(_fileSystem) { OnDirectoryExists = path => path == Folder ? new InvalidOperationException("odd") : null };

        var left = ForwardFolder.Rollback(faulty, Folder, staging, wasMissing: false, wasEmpty: true, _log);

        Assert.Contains(left, item => item.StartsWith("unknown", StringComparison.Ordinal));
        Assert.Contains(_log.Errors, error => error.Contains("check that folder by hand", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Infos, line => line.Contains("left unchanged"));
    }

    [Fact]
    public void A_folder_with_only_meta_ini_is_taken_over_and_meta_ini_survives_a_commit()
    {
        var meta = Path.Combine(Folder, ForwardFolder.MetaFileName);
        _fileSystem.AddFile(meta, new MockFileData("[General]\r\nnotes=mine"));
        Assert.False(ForwardFolder.IsForeign(_fileSystem, Folder));
        Assert.True(ForwardFolder.IsMissingOrEmpty(_fileSystem, Folder));

        var staging = ForwardFolder.BeginStaging(_fileSystem, Folder, _log);
        _fileSystem.AddFile(Path.Combine(staging, "CREDITS.md"), new MockFileData("new"));
        ForwardFolder.CommitStaging(_fileSystem, Folder, staging, _log);

        Assert.Equal("[General]\r\nnotes=mine", _fileSystem.File.ReadAllText(meta));
        Assert.Equal("new", _fileSystem.File.ReadAllText(Path.Combine(Folder, "CREDITS.md")));
    }

    [Fact]
    public void Meta_ini_stays_in_place_through_a_failed_swap()
    {
        var staging = ArrangeSwap();
        var meta = Path.Combine(Folder, ForwardFolder.MetaFileName);
        _fileSystem.AddFile(meta, new MockFileData("notes"));
        var faulty = new FaultyFileSystem(_fileSystem) { OnMove = (source, _) => IsStagedEntry(source, "Textures") ? new IOException("locked") : null };

        Assert.Throws<RunFailedException>(() => ForwardFolder.CommitStaging(faulty, Folder, staging, _log));

        Assert.Equal("notes", _fileSystem.File.ReadAllText(meta));
        AssertOldOutputIntact();
    }

    [Fact]
    public void A_previous_folder_that_appears_before_the_swap_fails_it_without_changing_anything()
    {
        var staging = ArrangeSwap();
        _fileSystem.AddFile(Path.Combine(Previous, "stranger.txt"), new MockFileData("theirs"));

        var ex = Assert.Throws<RunFailedException>(() => ForwardFolder.CommitStaging(_fileSystem, Folder, staging, _log));

        Assert.Contains(Previous, ex.Message, StringComparison.Ordinal);
        Assert.Equal("theirs", _fileSystem.File.ReadAllText(Path.Combine(Previous, "stranger.txt")));
        Assert.Equal("old", _fileSystem.File.ReadAllText(Path.Combine(Folder, "CREDITS.md")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(staging, @"Textures\new.dds")));
    }

    [Fact]
    public void A_file_named_like_the_previous_folder_is_a_leftover_too()
    {
        _fileSystem.AddFile(Marker, new MockFileData("marker"));
        _fileSystem.AddFile(Previous, new MockFileData("a file"));

        Assert.True(ForwardFolder.HasLeftoverPrevious(_fileSystem, Folder));
        var ex = Assert.Throws<RunFailedException>(() => ForwardFolder.BeginStaging(_fileSystem, Folder, _log));
        AssertDeleteAndRerunAdvice(ex.Message);
    }

    [Fact]
    public void A_failed_marker_write_surfaces_as_the_io_error()
    {
        var failure = new IOException("disk full");
        var faulty = new FaultyFileSystem(_fileSystem) { OnWriteAllText = path => path == Marker ? failure : null };

        var ex = Assert.Throws<IOException>(() => ForwardFolder.BeginStaging(faulty, Folder, _log));

        Assert.Same(failure, ex);
        Assert.False(_fileSystem.Directory.Exists(Staging));
    }

    [Fact]
    public void A_failed_staging_folder_creation_leaves_the_previous_output_alone()
    {
        _fileSystem.AddFile(Marker, new MockFileData("marker"));
        _fileSystem.AddFile(Path.Combine(Folder, "CREDITS.md"), new MockFileData("old"));
        var faulty = new FaultyFileSystem(_fileSystem) { OnCreateDirectory = path => path == Staging ? new IOException("denied") : null };

        Assert.Throws<IOException>(() => ForwardFolder.BeginStaging(faulty, Folder, _log));

        Assert.Equal("old", _fileSystem.File.ReadAllText(Path.Combine(Folder, "CREDITS.md")));
        Assert.False(_fileSystem.Directory.Exists(Staging));
    }

    [Fact]
    public void A_non_empty_folder_without_the_marker_fails_the_run_and_is_left_alone()
    {
        _fileSystem.AddFile(Path.Combine(Folder, "someone_elses.esp"), new MockFileData([3]));

        var ex = Assert.Throws<RunFailedException>(() => ForwardFolder.BeginStaging(_fileSystem, Folder, _log));

        Assert.Contains("another mod", ex.Message);
        Assert.Equal([Path.Combine(Folder, "someone_elses.esp")], _fileSystem.Directory.EnumerateFileSystemEntries(Folder).ToArray());
        Assert.True(ForwardFolder.IsForeign(_fileSystem, Folder));
    }

    [Fact]
    public void An_empty_folder_without_the_marker_is_taken_over()
    {
        _fileSystem.AddDirectory(Folder);

        ForwardFolder.BeginStaging(_fileSystem, Folder, _log);

        Assert.True(_fileSystem.File.Exists(Marker));
    }
}
