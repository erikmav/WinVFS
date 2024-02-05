using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WinVfs.VirtualFilesystem;

/// <summary>
/// Virtual filesystem callbacks for a component that deals with a DirectoryMetadata cache and
/// runs async callbacks for file content and missing directory metadata.
/// </summary>
public interface IVfsCallbacks
{
    /// <summary>
    /// Tries to get a directory cache entry. This method must return quickly to avoid holding a ProjFS
    /// callback context for an extended period. <see cref="RequestDirectoryMetadataAsync"/> will be
    /// called if this call returns false.
    /// </summary>
    /// <param name="virtualRelativePath">The directory to retrieve, relative to the virtualization root.</param>
    /// <param name="directoryMetadata">
    /// The returned directory information, or null if not available. NOTE: The filesystem entries in this directory must
    /// be sorted according to the OS-specific ordering. For Windows you must use <see cref="ProjFsPathStringComparer"/>
    /// to ensure NTFS ordering of entries; <see cref="StringComparer.OrdinalIgnoreCase"/> is incorrect to use.
    /// </param>
    /// <returns>True if a cache entry was found, false if <see cref="RequestDirectoryMetadataAsync"/> should be called to retrieve metadata.</returns>
    bool TryGetDirectoryMetadataCacheEntry(string virtualRelativePath, [NotNullWhen(true)] out IDirectoryMetadata? directoryMetadata);

    /// <summary>
    /// Called to dynamically retrieve directory metadata when <see cref="TryGetDirectoryMetadataCacheEntry"/> returns false.
    /// </summary>
    /// <param name="virtualRelativePath">The directory to retrieve, relative to the virtualization root.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<IDirectoryMetadata> RequestDirectoryMetadataAsync(string virtualRelativePath, CancellationToken cancellationToken);

    /// <summary>
    /// Called to advise of an enumeration of a virtualized directory.
    /// </summary>
    /// <param name="virtualRelativePath">The directory enumerated, relative to the virtualization root.</param>
    /// <param name="enumerationPattern">The file enumeration pattern including wildcard characters.</param>
    void OnDirectoryEnumeration(string virtualRelativePath, string enumerationPattern);

    /// <summary>
    /// Called to retrieve the contents of a virtualized file as a stream.
    /// </summary>
    /// <param name="virtualRelativePath">The file to retrieve, relative to the virtualization root.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A stream containing the file content. The stream will be closed automatically.</returns>
    /// <exception cref="FileNotFoundException">The file is not in the client filesystem.</exception>
    ValueTask<Stream> GetFileAsync(string virtualRelativePath, CancellationToken cancellationToken);

    /// <summary>
    /// Called on an unrecoverable error, typically an unhandled exception thrown from a callback or
    /// another exception from within the virtualization core or ProjFS itself.
    /// This should result in a general failure in this virtualization session.
    /// </summary>
    /// <param name="exception">
    /// A related exception containing information about the failure. The exception's Message property
    /// contains a summary of the problem, and typically there will be an InnerException containing more detail.
    /// </param>
    void FatalErrorOccurred(Exception exception);
}
