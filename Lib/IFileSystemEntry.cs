using System;

namespace WinVfs.VirtualFilesystem;

/// <summary>
/// Required properties for a file or directory entry in the virtual filesystem.
/// </summary>
public interface IFileSystemEntry
{
    /// <summary>
    /// True if this entry represents a directory, false for a file.
    /// </summary>
    bool IsDirectory { get; }

    /// <summary>
    /// Gets the file size. Ignored when <see cref="IsDirectory"/> is true.
    /// </summary>
    long FileSize { get; }

    /// <summary>
    /// The file or directory name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The UTC time when a file was last updated.
    /// This value, if present, will be used as the creation and last-update time for the file when rendered into the VFS.
    /// If not provided, a default of the session start time is used instead.
    /// </summary>
    DateTime? LastUpdateTimeUtc { get; }

    public string? ToString()
    {
        return IsDirectory ? $"{Name} (dir)" : $"{Name} (file, size {FileSize})";
    }
}
