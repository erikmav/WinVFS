using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinVfs.VirtualFilesystem;

namespace WinVfs.Service;

/// <summary>
/// Callbacks for the case where a source directory is mirrored into a virtualization root.
/// </summary>
internal sealed class MirroredDirectoryVfsCallbacks : IVfsCallbacks
{
    private readonly object _lockObj = new();
    private readonly Random _random = new();
    
    public MirroredDirectoryVfsCallbacks(string sourceDir)
    {
        SourceDir = sourceDir;
    }
    
    public string SourceDir { get; }

    public bool TryGetDirectoryMetadataCacheEntry(string virtualRelativePath, [NotNullWhen(true)] out IDirectoryMetadata? directoryMetadata)
    {
        // Randomly return a directory here vs. RequestDirectoryMetadataAsync to exercise both paths.
        int r;
        lock (_lockObj)
        {
            r = _random.Next();
        }

        if (r == 0)
        {
            Console.WriteLine($"TryGetDirMetadataCacheEntry returning results for: {virtualRelativePath}");
            directoryMetadata = CreateDirMetadataFromRealDir(Path.Combine(SourceDir, virtualRelativePath));
            return true;
        }

        Console.WriteLine($"TryGetDirMetadataCacheEntry randomly not returning results for: {virtualRelativePath}");
        directoryMetadata = null;
        return false;
    }

    public ValueTask<IDirectoryMetadata> RequestDirectoryMetadataAsync(string virtualRelativePath, CancellationToken cancellationToken)
    {
        Console.WriteLine($"RequestDirMetadata: {virtualRelativePath}");
        return ValueTask.FromResult((IDirectoryMetadata)CreateDirMetadataFromRealDir(Path.Combine(SourceDir, virtualRelativePath)));
    }

    private DirectoryMetadata CreateDirMetadataFromRealDir(string relativePath)
    {
        var sourceDir = new DirectoryInfo(Path.Combine(SourceDir, relativePath));
        var metadata = new DirectoryMetadata(relativePath, exists: sourceDir.Exists);
        if (sourceDir.Exists)
        {
            foreach (FileSystemInfo fsi in sourceDir.EnumerateFileSystemInfos())
            {
                if (fsi.Attributes.HasFlag(FileAttributes.Directory))
                {
                    metadata.WritableEntries.Add(new FileSystemEntry(fsi.Name));
                }
                else
                {
                    var fi = (FileInfo)fsi;
                    metadata.WritableEntries.Add(new FileSystemEntry(fsi.Name, fi.Length, fi.LastWriteTimeUtc));
                }
            }
        }

        return metadata;
    }

    public void OnDirectoryEnumeration(string virtualRelativePath, string enumerationPattern)
    {
        Console.WriteLine($"Dir enum in '{virtualRelativePath}' with pattern '{enumerationPattern}'");
    }

    public ValueTask<Stream> GetFileAsync(string virtualRelativePath, CancellationToken cancellationToken)
    {
        Console.WriteLine($"GetFile: {virtualRelativePath}");
        string filePath = Path.Combine(SourceDir, virtualRelativePath);
        return ValueTask.FromResult((Stream)File.OpenRead(filePath));
    }

    public void FatalErrorOccurred(Exception exception)
    {
        Console.Error.WriteLine($"ERROR: Fatal virtualization error occurred: {exception}");
    }
}
