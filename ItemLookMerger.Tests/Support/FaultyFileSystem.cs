using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace ItemLookMerger.Tests.Support;

/// <summary>
/// An in-memory file system whose moves and deletes can be made to fail: each hook sees the paths and returns the exception to throw
/// (or null). Everything else goes to the wrapped <see cref="MockFileSystem"/>, so the test arranges and checks through that one.
/// </summary>
internal sealed class FaultyFileSystem : IFileSystem
{
    private readonly MockFileSystem _inner;

    public FaultyFileSystem(MockFileSystem inner)
    {
        _inner = inner;
        File = new FaultyFile(inner, this);
        Directory = new FaultyDirectory(inner, (FileBase)File, this);
    }

    /// <summary>Called with (source, target) before a file or folder is moved.</summary>
    public Func<string, string, Exception?>? OnMove { get; set; }

    /// <summary>Called with the path before a file or folder is deleted.</summary>
    public Func<string, Exception?>? OnDelete { get; set; }

    /// <summary>Called with the path before text is written to a file.</summary>
    public Func<string, Exception?>? OnWriteAllText { get; set; }

    /// <summary>Called with the path before a folder is created.</summary>
    public Func<string, Exception?>? OnCreateDirectory { get; set; }

    /// <summary>Called with the path before a folder's existence is checked; for faulting the code that sits outside its own try blocks.</summary>
    public Func<string, Exception?>? OnDirectoryExists { get; set; }

    /// <summary>
    /// Called with the current path of each file: true means it is held open, so moving or deleting that file, or any folder that holds
    /// it (however deep), fails with an <see cref="IOException"/>. The path is the one the file has at that moment, so a file can be
    /// locked only in the previous folder, say.
    /// </summary>
    public Func<string, bool>? IsLocked { get; set; }

    public IFile File { get; }
    public IDirectory Directory { get; }
    public IFileInfoFactory FileInfo => _inner.FileInfo;
    public IFileStreamFactory FileStream => _inner.FileStream;
    public IPath Path => _inner.Path;
    public IDirectoryInfoFactory DirectoryInfo => _inner.DirectoryInfo;
    public IDriveInfoFactory DriveInfo => _inner.DriveInfo;
    public IFileSystemWatcherFactory FileSystemWatcher => _inner.FileSystemWatcher;
    public IFileVersionInfoFactory FileVersionInfo => _inner.FileVersionInfo;

    /// <summary>The exception for a locked file at or below <paramref name="path"/>, or null.</summary>
    private Exception? LockFault(string path)
    {
        if (IsLocked == null) return null;
        var locked = _inner.File.Exists(path)
            ? IsLocked(path)
            : _inner.Directory.Exists(path) && _inner.Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any(IsLocked);
        return locked ? new IOException($"A file at or below {path} is in use by another process.") : null;
    }

    private sealed class FaultyFile(MockFileSystem inner, FaultyFileSystem owner) : MockFile(inner)
    {
        public override void Move(string sourceFileName, string destFileName)
        {
            if ((owner.OnMove?.Invoke(sourceFileName, destFileName) ?? owner.LockFault(sourceFileName)) is { } fault) throw fault;
            base.Move(sourceFileName, destFileName);
        }

        public override void Delete(string path)
        {
            if ((owner.OnDelete?.Invoke(path) ?? owner.LockFault(path)) is { } fault) throw fault;
            base.Delete(path);
        }

        public override void WriteAllText(string path, string? contents)
        {
            if (owner.OnWriteAllText?.Invoke(path) is { } fault) throw fault;
            base.WriteAllText(path, contents);
        }
    }

    private sealed class FaultyDirectory(MockFileSystem inner, FileBase file, FaultyFileSystem owner) : MockDirectory(inner, file, @"C:\")
    {
        public override IDirectoryInfo CreateDirectory(string path)
        {
            if (owner.OnCreateDirectory?.Invoke(path) is { } fault) throw fault;
            return base.CreateDirectory(path);
        }

        public override bool Exists(string? path)
        {
            if (path != null && owner.OnDirectoryExists?.Invoke(path) is { } fault) throw fault;
            return base.Exists(path);
        }

        public override void Move(string sourceDirName, string destDirName)
        {
            if ((owner.OnMove?.Invoke(sourceDirName, destDirName) ?? owner.LockFault(sourceDirName)) is { } fault) throw fault;
            base.Move(sourceDirName, destDirName);
        }

        public override void Delete(string path)
        {
            if ((owner.OnDelete?.Invoke(path) ?? owner.LockFault(path)) is { } fault) throw fault;
            base.Delete(path);
        }

        public override void Delete(string path, bool recursive)
        {
            if ((owner.OnDelete?.Invoke(path) ?? owner.LockFault(path)) is { } fault) throw fault;
            base.Delete(path, recursive);
        }
    }
}
