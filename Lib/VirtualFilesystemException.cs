using System;

namespace WinVfs.VirtualFilesystem;

/// <summary>
/// The exception used when a fatal exception was encountered.
/// </summary>
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
