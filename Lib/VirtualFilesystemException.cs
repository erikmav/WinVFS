using System;

namespace WinVfs.VirtualFilesystem;

public sealed class VirtualFilesystemException : Exception
{
    public VirtualFilesystemException()
    {
    }

    public VirtualFilesystemException(string message)
        : base(message)
    {
    }

    public VirtualFilesystemException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
