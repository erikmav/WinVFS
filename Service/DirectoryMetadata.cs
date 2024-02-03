using System;
using System.Collections.Generic;
using System.Linq;
using WinVfs.VirtualFilesystem;

namespace WinVfs.Service;

internal sealed class DirectoryMetadata : IDirectoryMetadata
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