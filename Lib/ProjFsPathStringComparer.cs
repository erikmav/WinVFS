using System;

namespace WinVfs.VirtualFilesystem;

/// <summary>
/// Implements a string comparer using the NTFS filesystem comparer used by ProjFS.
/// </summary>
/// <remarks>
/// The NTFS comparer has subtle differences in the higher Unicode ranges, but the most glaring
/// difference is that it considers the dot '.' to be greater than most other symbols despite being a
/// relatively low value in ASCII.
/// </remarks>
public sealed class ProjFsPathStringComparer : StringComparer
{
    public static ProjFsPathStringComparer Instance { get; } = new();

    public override int Compare(string? x, string? y)
    {
        if (x is null)
        {
            return (y is null) ? 0 : 1;
        }

        if (y is null)
        {
            return -1;
        }

        return Microsoft.Windows.ProjFS.Utils.FileNameCompare(x, y);
    }

    public override bool Equals(string? x, string? y)
    {
        return Compare(x, y) == 0;
    }

    public override int GetHashCode(string obj)
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(obj);
    }
}
