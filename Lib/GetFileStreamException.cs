using System;
using Microsoft.Windows.ProjFS;

namespace WinVfs.VirtualFilesystem;

/// <summary>
/// The exception thrown when a file stream cannot be retrieved.
/// </summary>
public sealed class GetFileStreamException : Exception
{
    public GetFileStreamException()
    {
    }

    public GetFileStreamException(string message)
        : base(message)
    {
    }

    public GetFileStreamException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GetFileStreamException(HResult errorCode, bool isFatalError)
        : this($"{nameof(GetFileStreamException)}, error: {errorCode}", errorCode)
    {
        IsFatalError = isFatalError;
    }

    public GetFileStreamException(string message, HResult result)
        : base(message)
    {
        HResult = (int)result;
    }

    /// <summary>
    /// When false, the related error is expected e.g. as a common race condition in the filesystem
    /// such as when we go to write the file contents and ProjFS indicates the file handle is closed.
    /// When true, the related error was unexpected and should result in aborting the ProjFS session
    /// and causing the command to be run at the client.
    /// </summary>
    public bool IsFatalError { get; }
}
