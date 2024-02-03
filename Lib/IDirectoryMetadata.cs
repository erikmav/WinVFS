using System;
using System.Collections.Generic;
using System.IO;

namespace WinVfs.VirtualFilesystem;

/// <summary>
/// Defines the properties required for representing a directory in the virtual filesystem.
/// </summary>
public interface IDirectoryMetadata
{
    /// <summary>
    /// The relative path from the virtual filesystem root for this directory.
    /// An empty string indicates directory metadata for the VFS root directory.
    /// </summary>
    string RelativeBasePath { get; }

    /// <summary>
    /// Gets whether the directory exists. This is typically true but can be false as part of caching negative entries.
    /// </summary>
    bool Exists { get; }

    IReadOnlyList<IFileSystemEntry> Entries { get; }

    /// <summary>
    /// Gets a filesystem entry by its name, or null if the entry is not present in the directory.
    /// </summary>
    /// <remarks>This method allows the implementation to provide efficient lookups, e.g. with a dictionary, as needed.</remarks>
    IFileSystemEntry? GetEntryOrNull(string fileOrDirName);
}

public static class DirectoryMetadataExtensions
{
    /// <summary>
    /// Verifies that an <see cref="IDirectoryMetadata"/> contains entries in ProjFS-required sort order.
    /// </summary>
    public static void ValidateProjFsSortOrderOfEntries(this IDirectoryMetadata dm)
    {
        for (int i = 1; i < dm.Entries.Count; i++)
        {
            string name1 = dm.Entries[i - 1].Name;
            string name2 = dm.Entries[i].Name;
            if (ProjFsPathStringComparer.Instance.Compare(name1, name2) >= 0)
            {
                throw new InvalidDataException(
                    $"DirectoryMetadata for '{dm.RelativeBasePath}' contains out-of-order or duplicate entries - ensure they are sorted using {nameof(ProjFsPathStringComparer)}. Entries out of order: {name1}, {name2}");
            }
        }
    }
}
