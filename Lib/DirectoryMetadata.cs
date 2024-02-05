using System;
using System.Collections.Generic;
using System.Linq;

namespace WinVfs.VirtualFilesystem;

/// <summary>
/// Default implementation of <see cref="IDirectoryMetadata"/> with an O(N) lookup in <see cref="GetEntryOrNull"/>
/// and a non-thread-safe writeable collection of entries.
/// </summary>
public sealed class DirectoryMetadata : IDirectoryMetadata
{
    public DirectoryMetadata(string relativeBasePath, bool exists = true)
    {
        RelativeBasePath = relativeBasePath;
        Exists = exists;
    }

    public string RelativeBasePath { get; }
    public bool Exists { get; }

    public List<IFileSystemEntry> WritableEntries { get; } = new();
    public IReadOnlyList<IFileSystemEntry> Entries => WritableEntries;
    
    public IFileSystemEntry? GetEntryOrNull(string fileOrDirName)
    {
        return WritableEntries.FirstOrDefault(e => e.Name.Equals(fileOrDirName, StringComparison.OrdinalIgnoreCase));
    }
}
