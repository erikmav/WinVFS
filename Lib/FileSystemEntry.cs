﻿using System;

namespace WinVfs.VirtualFilesystem;

/// <summary>
/// Default implementation of <see cref="IFileSystemEntry"/>
/// </summary>
public sealed class FileSystemEntry : IFileSystemEntry
{
    public FileSystemEntry(string dirName)
    {
        Name = dirName;
        IsDirectory = true;
        FileSize = -1;
    }

    public FileSystemEntry(string fileName, long fileSize, DateTime? lastUpdateTimeUtc = null)
    {
        Name = fileName;
        FileSize = fileSize;
        LastUpdateTimeUtc = lastUpdateTimeUtc;
    }

    public bool IsDirectory { get; }
    public long FileSize { get; }
    public string Name { get; }
    public DateTime? LastUpdateTimeUtc { get; }
}
