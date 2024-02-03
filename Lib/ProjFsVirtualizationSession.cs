using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.Windows.ProjFS;

namespace WinVfs.VirtualFilesystem;

public sealed class ProjFsVirtualizationSession : IDisposable
{
    const long DirectorySize = -1; // File size used for directory placeholders
    const FileAttributes DirAttr = FileAttributes.Directory; // file attributes used for directory placeholders
    const FileAttributes FileAttr = FileAttributes.Archive; // file attributes used for file placeholders

    private static bool FirstStartCompleted;
    private static readonly object FirstStartLock = new();

    // Use a non-empty array of one mapping that shuts off all notifications.
    // Just an empty array defaults to several types of notifications we don't use, slowing down virtualization.
    private static readonly NotificationMapping[] ZeroNotifications = { new NotificationMapping(NotificationType.None, notificationRoot: string.Empty /*virtualization root*/) };

    // We must have several open reads active to the kernel to allow quick transfer of PrjFlt requests to user mode without pending I/Os in the kernel.
    // With ProjFS notifications turned off we mainly get individual callbacks for file/dir actions from threads running in processes accessing the VFS.
    private const int MinProjFsHandlingThreads = 5;
    private readonly uint ThreadCount = (uint)Math.Max(MinProjFsHandlingThreads, Environment.ProcessorCount * 2);

    private readonly string _virtualizationRootDir;
    private readonly ILogger _logger;
    private readonly DateTime _sessionStartTimeUtc;
    private readonly ProjFsOptions _options;
    private readonly VirtualizationInstance _projFs;
    private readonly Task _asyncOperationObservationTask;
    private readonly BufferBlock<Task> _asyncOperationsNeedingObservation = new(new DataflowBlockOptions { EnsureOrdered = false });
    private readonly CancellationTokenSource _disposeCancellationTokenSource = new();
    private readonly CancellationTokenSource _combinedCts;
    private readonly ProjFsCallbackHandler _projFsCallbackHandler;

    /// <summary>
    /// Constructs and starts a projFS virtualization session centered on a directory.
    /// </summary>
    /// <param name="virtualizationRootDir">The full path to the root directory.</param>
    /// <param name="callbacks">Callbacks required to answer virtualization queries.</param>
    /// <param name="logger">Logger for receiving diagnostic traces.</param>
    /// <param name="sessionStartTimeUtc">The timestamp to use for all directory entries.</param>
    /// <param name="options">Session options.</param>
    /// <param name="ct">A cancellation token for the overall session.</param>
    public ProjFsVirtualizationSession(
        string virtualizationRootDir,
        IVfsCallbacks callbacks,
        ILogger logger,
        DateTime sessionStartTimeUtc,
        ProjFsOptions options,
        CancellationToken ct)
    {
        _virtualizationRootDir = virtualizationRootDir;
        _logger = logger;
        _sessionStartTimeUtc = sessionStartTimeUtc;
        _options = options;

        // Cancel operations either when the user's cancellation token is signaled or we get a Dispose() call.
        _combinedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCancellationTokenSource.Token);

        _projFs = new VirtualizationInstance(
            virtualizationRootDir,
            ThreadCount,
            ThreadCount,
            true,
            ZeroNotifications);
        _projFsCallbackHandler = new ProjFsCallbackHandler(
            _projFs,
            callbacks,
            logger,
            sessionStartTimeUtc,
            options,
            _asyncOperationsNeedingObservation,
            Stats,
            _combinedCts.Token);
        _logger.Debug($"ProjFS: Starting virtualization for '{virtualizationRootDir}' (detailed trace: {options.EnableDetailedTrace})");

        // Serialize first start call to work around startup/init race in ProjFS.
        // Calls are typically ~3msec so this can cause a short delay to early calls just after service start.
        HResult hr;
        if (!Volatile.Read(ref FirstStartCompleted))
        {
            lock (FirstStartLock)
            {
                hr = _projFs.StartVirtualizing(_projFsCallbackHandler);
                FirstStartCompleted = true;
            }
        }
        else
        {
            hr = _projFs.StartVirtualizing(_projFsCallbackHandler);
        }

        if (hr != HResult.Ok)
        {
            string message = $"ProjFS: Failed to initialize the ProjFS subsystem, hr=0x%{hr:X} (hr)";
            _logger.Error(message);
            throw new InvalidOperationException(message);
        }

        _asyncOperationObservationTask = Task.Run(
            async () =>
            {
                while (true)
                {
                    while (_asyncOperationsNeedingObservation.TryReceive(out Task? operationTask))
                    {
                        try
                        {
                            await operationTask;
                        }
                        catch (TaskCanceledException)
                        {
                            _logger.Debug($"ProjFS: TaskCanceledException from async operation task for {_virtualizationRootDir}");
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, $"ProjFS: Exception from async operation task for {_virtualizationRootDir}");
                        }
                    }

                    bool moreAvailable = await _asyncOperationsNeedingObservation.OutputAvailableAsync();

                    if (!moreAvailable)
                    {
                        break;
                    }
                }
            }, CancellationToken.None);
    }

    public void Dispose()
    {
        _logger.Debug($"ProjFS: Finalizing in-progress async calls prior to shutting down virtualization for {_virtualizationRootDir}");
        _disposeCancellationTokenSource.Cancel(throwOnFirstException: true);
        _asyncOperationsNeedingObservation.Complete();
        try
        {
            _asyncOperationObservationTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Exception from async operation observation task for {_virtualizationRootDir}");
        }

        _logger.Debug($"ProjFS: Shutting down virtualization for {_virtualizationRootDir}");
        _projFs.StopVirtualizing();
        _logger.Debug($"ProjFS: Virtualization shut down. Stats: {Environment.NewLine}{Stats}");

        _disposeCancellationTokenSource.Dispose();
        _combinedCts.Dispose();

        _projFsCallbackHandler.Dispose();
    }

    public ProjFsStats Stats { get; } = new();

    /// <summary>
    /// Marks an existing directory as being part of virtualization and optionally fills with file and directory
    /// placeholders except where directories or files already exist. The physical directory should have been
    /// created before starting the virtualization session.
    /// </summary>
    /// <param name="virtualRelativePath">The relative path of the directory within the root directory.</param>
    /// <param name="dirEntriesToRender">
    /// The directory contents to pre-render.
    /// Placeholders are best pre-created in directories where full *.* directory enumerations
    /// are known to be executed during virtualization, such that all placeholders in the directory
    /// end up being rendered; pre-rendering in such a case avoids expensive kernel callbacks.
    /// ProjFS is careful about minimizing calls to get information for placeholders, so pre-rendering
    /// the full contents of a directory generally is not advisable without performance testing.
    /// </param>
    /// <param name="directoryFullyRendered">
    /// When false, the directory contents are not complete and we should ask ProjFS to allow callbacks for more information.
    /// When true, all subdirectories and files have been materialized and we should turn off further ProjFS callbacks,
    /// improving performance.
    /// </param>
    internal void MaterializeDirectory(string virtualRelativePath, IEnumerable<IFileSystemEntry> dirEntriesToRender, bool directoryFullyRendered)
    {
        string dirPath;
        bool markAsPlaceholder;
        if (virtualRelativePath.Length == 0)
        {
            dirPath = _virtualizationRootDir;
            markAsPlaceholder = false;  // Virtualization root cannot be marked as a materialized placeholder.
        }
        else
        {
            dirPath = Path.Combine(_virtualizationRootDir, virtualRelativePath);
            markAsPlaceholder = !directoryFullyRendered;
        }

        foreach (IFileSystemEntry entry in dirEntriesToRender)
        {
            CreatePlaceholder(virtualRelativePath, entry);
        }

        if (_options.EnableDetailedTrace)
        {
            _logger.Finest($"{(markAsPlaceholder ? "Marking" : "Not marking")} directory '{dirPath}' as materialized placeholder for ProjFS");
        }

        if (markAsPlaceholder)
        {
            MarkRealDirectoryAsMaterializedPlaceholderImpl(dirPath, virtualRelativePath);
        }
    }

    /// <summary>
    /// Creates a ProjFS placeholder (reparse point) in the filesystem within an existing directory.
    /// </summary>
    internal void CreatePlaceholder(string virtualRelativeDirPath, IFileSystemEntry entry)
    {
        string virtualRelativeEntryPath = Path.Combine(virtualRelativeDirPath, entry.Name);
        if (entry.IsDirectory)
        {
            CreateDirPlaceholder(virtualRelativeEntryPath, entry);
        }
        else
        {
            CreateFilePlaceholder(virtualRelativeEntryPath, entry);
        }
    }

    public void CreateFilePlaceholder(string virtualRelativePath, IFileSystemEntry entry)
    {
#if DEBUG
        if (entry.IsDirectory)
        {
            throw new ArgumentException($"Cannot create a file placeholder for a directory entry: {entry}");
        }
#endif

        CreatePlaceholder(virtualRelativePath, entry, FileAttr, entry.FileSize);
    }

    public void CreateDirPlaceholder(string virtualRelativePath, IFileSystemEntry entry)
    {
#if DEBUG
        if (!entry.IsDirectory)
        {
            throw new ArgumentException($"Cannot create a directory placeholder for a file entry: {entry}");
        }
#endif

        CreatePlaceholder(virtualRelativePath, entry, DirAttr, DirectorySize);
    }

    private static HResult CreatePlaceholder(VirtualizationInstance projFs, string virtualRelativeEntryPath, IFileSystemEntry entry, DateTime timestamp, ILogger logger)
    {
        return entry.IsDirectory
            ? CreatePlaceholder(projFs, virtualRelativeEntryPath, entry, timestamp, DirAttr, DirectorySize, logger)
            : CreatePlaceholder(projFs, virtualRelativeEntryPath, entry, timestamp, FileAttr, entry.FileSize, logger);
    }

    private static HResult CreatePlaceholder(VirtualizationInstance projFs, string virtualRelativeEntryPath, IFileSystemEntry entry, DateTime timestamp, FileAttributes fileAttributes, long endOfFile, ILogger logger)
    {
        // NOTE: 'fileAttributes' and 'endOfFile' are specified explicitly as arguments (as opposed to being computed here based on 'entry.IsDirectory') to avoid as many data dependencies
        // in this method whenever possible. Concretely, the caller might already know if the entry in question is a file or a directory, and so it can provide expected values for those arguments.
#if DEBUG
        FileAttributes expectedAttributes = entry.IsDirectory ? DirAttr : FileAttr;
        if (expectedAttributes != fileAttributes)
        {
            throw new ArgumentException($"Mismatched file attributes; Expected: {expectedAttributes}, Actual: {fileAttributes}");
        }

        long expectedEndOfFile = entry.IsDirectory ? DirectorySize : entry.FileSize;
        if (expectedEndOfFile != endOfFile)
        {
            throw new ArgumentException($"Mismatched file size; Expected: {expectedEndOfFile}, Actual: {endOfFile}");
        }
#endif

        DateTime lastUpdateTime = entry.LastUpdateTimeUtc ?? timestamp;
        HResult hr = projFs.WritePlaceholderInfo(
            virtualRelativeEntryPath,
            lastUpdateTime,
            lastUpdateTime,
            lastUpdateTime,
            lastUpdateTime,
            fileAttributes,
            endOfFile,
            isDirectory: entry.IsDirectory,
            contentId: null,
            providerId: null);
        if (hr != HResult.Ok)
        {
            string message = $"{nameof(projFs.WritePlaceholderInfo)} for {entry} failed with HResult 0x{hr:X}";
            logger.Error($"ProjFS: {message}");
            throw new Win32Exception((int)hr, message);
        }

        if (logger.IsFinestLoggingEnabled)
        {
            logger.Finest($"WritePlaceholderInfo2 created placeholder for {entry}");
        }

        return hr;
    }

    /// <summary>
    /// Creates a ProjFS placeholder (reparse point) in the filesystem within an existing directory.
    /// </summary>
    public void CreatePlaceholder(string virtualRelativeEntryPath, IFileSystemEntry entry, FileAttributes attributes, long endOfFile)
        => CreatePlaceholder(_projFs, virtualRelativeEntryPath, entry, _sessionStartTimeUtc, attributes, endOfFile, _logger);

    /// <summary>
    /// Tells ProjFS to set the specified already-created directory under the virtualization root to a
    /// materialized placeholder so that directory enumeration callbacks for that directory and its
    /// subdirectories will be performed. This can be used to "pre-render" directories by creating
    /// them before starting virtualization, then calling this method to activate them within the VFS.
    /// </summary>
    /// <param name="virtualRelativePath">The directory path relative to the virtual root.</param>
    public void MarkRealDirectoryAsMaterializedPlaceholder(string virtualRelativePath)
    {
        if (virtualRelativePath.Length == 0)
        {
            // Virtualization root cannot be marked as a materialized placeholder.
            return;
        }

        string dirPath = Path.Combine(_virtualizationRootDir, virtualRelativePath);
        MarkRealDirectoryAsMaterializedPlaceholderImpl(dirPath, virtualRelativePath);
    }

    private void MarkRealDirectoryAsMaterializedPlaceholderImpl(string vfsRootedPath, string virtualRelativePath)
    {
        HResult hr = _projFs.MarkDirectoryAsPlaceholder(vfsRootedPath, null, null);
        if (hr != HResult.Ok)
        {
            string message = $"MarkDirectoryAsPlaceholder for '{virtualRelativePath}' failed with HResult 0x{hr:X}";
            _logger.Error("ProjFS: {message}");
            throw new Win32Exception((int)hr, message);
        }
    }

    /// <summary>
    /// Implements directory and file callbacks from ProjFS.
    /// </summary>
    internal sealed class ProjFsCallbackHandler : IRequiredCallbacks, IDisposable
    {
        /// <summary>
        /// Handles state for enumeration of the entries in a DirectoryMetadata with filtering.
        /// </summary>
        internal sealed class DirectoryEnumeration
        {
            // The filter string for the enumeration once it is known. May be null which implies a "*" filter.
            private string? _filter;

            // Whether the filter has been set by a call to SetFilter() or ResetEnumeration(),
            // used to prevent resetting the value on later calls.
            private bool _filterSet;

            // The index within the metadata entries containing the current filesystem entry.
            private int _currentIndex;

            public DirectoryEnumeration(string virtualRelativePath, IDirectoryMetadata dirMetadata)
            {
                VirtualRelativePath = virtualRelativePath;
                DirectoryMetadata = dirMetadata;

#if DEBUG
                dirMetadata.ValidateProjFsSortOrderOfEntries();
#endif
            }

            /// <summary>
            /// Gets the directory path being enumerated relative to the virtualization root.
            /// </summary>
            public string VirtualRelativePath { get; }

            /// <summary>
            /// Gets the directory metadata for the enumeration.
            /// </summary>
            public IDirectoryMetadata DirectoryMetadata { get; }

            /// <summary>
            /// The enumeration filter string in a "nice" format (null and empty ProjFS filters are converted to "*").
            /// </summary>
            public string Filter => string.IsNullOrEmpty(_filter) ? "*" : _filter;

            /// <summary>
            /// Gets the current IFileSystemEntry for this enumeration, or null if the enumeration is complete.
            /// </summary>
            public IFileSystemEntry? GetCurrentMatchingOrNull()
            {
                if (_currentIndex < DirectoryMetadata.Entries.Count)
                {
                    return DirectoryMetadata.Entries[_currentIndex];
                }

                return null;
            }

            /// <summary>
            /// Called when a directory enumeration is restarted by the filesystem or ProjFS, possibly with a new filter string.
            /// </summary>
            /// <param name="filter">A file or directory name filter string that can contain wildcards.</param>
            public void ResetEnumeration(string filter)
            {
                _currentIndex = 0;
                SetFilterImpl(filter);
            }

            /// <summary>
            /// Called to set the filter string. This value is not known when the directory enumeration
            /// starts, but is provided in the first and later calls to get blocks of directory metadata
            /// entries. Typically the second and later enumeration calls provide a null or empty filter,
            /// so we accept only the first filter value.
            /// </summary>
            /// <param name="filter">A file or directory name filter string that can contain wildcards.</param>
            /// <returns>True if the filter was updated.</returns>
            public bool SetFilter(string filter)
            {
                // Directory enumeration callbacks for successive calls to FindNextFile()
                // usually pass a null or empty string. Only set the filter if it has not already been set.
                // Note nulls are allowed as valid values so we need a boolean for tracking.
                if (!_filterSet)
                {
                    SetFilterImpl(filter);
                    return true;
                }

                return false;
            }

            // Sets the filter string without checking the current value of _filter.
            private void SetFilterImpl(string filter)
            {
                if (filter.Length == 0 ||
                    filter.Equals("*", StringComparison.Ordinal) ||
                    filter.Equals("*.*", StringComparison.Ordinal))
                {
                    _filter = null;
                }
                else
                {
                    _filter = filter;
                    AdvanceToNextMatching();
                }

                _filterSet = true;
            }

            /// <summary>
            /// Advances the enumeration to the next matching item.
            /// </summary>
            /// <returns>True if there is a next item, false if the enumeration is complete.</returns>
            public bool MoveNext()
            {
                _currentIndex++;
                return AdvanceToNextMatching();
            }

            private bool AdvanceToNextMatching()
            {
                IReadOnlyList<IFileSystemEntry> entries = DirectoryMetadata.Entries;
                while (_currentIndex < entries.Count)
                {
                    IFileSystemEntry entry = entries[_currentIndex];
                    if (_filter == null || PatternMatcher.StrictMatchPattern(_filter, entry.Name))
                    {
                        return true;
                    }

                    _currentIndex++;
                }

                return false;
            }
        }

        private const int FileBufferSize = 64 * 1024;

        private readonly struct FileReadBufferHandle() : IDisposable
        {
            public void Dispose()
            {
                ArrayPool<byte>.Shared.Return(Buffer);
            }

            public byte[] Buffer { get; } = ArrayPool<byte>.Shared.Rent(FileBufferSize);
        }

        private readonly VirtualizationInstance _projFs;
        private readonly ConcurrentDictionary<Guid, DirectoryEnumeration> _currentDirectoryEnumerations = new();
        private readonly IVfsCallbacks _callbacks;
        private readonly ILogger _logger;
        private readonly DateTime _sessionStartTimeUtc;
        private readonly ProjFsOptions _options;
        private readonly BufferBlock<Task> _asyncOperationsNeedingObservation;
        private readonly ProjFsStats _stats;
        private readonly CancellationToken _ct;
        private readonly ConcurrentStack<WriteBuffer> _writeBufferPool = new();

        public ProjFsCallbackHandler(
            VirtualizationInstance projFs,
            IVfsCallbacks callbacks,
            ILogger logger,
            DateTime sessionStartTimeUtc,
            ProjFsOptions options,
            BufferBlock<Task> asyncOperationsNeedingObservation,
            ProjFsStats stats,
            CancellationToken ct)
        {
            _projFs = projFs;
            _callbacks = callbacks;
            _logger = logger;
            _sessionStartTimeUtc = sessionStartTimeUtc;
            _options = options;
            _asyncOperationsNeedingObservation = asyncOperationsNeedingObservation;
            _stats = stats;
            _ct = ct;

            _projFs.OnQueryFileName = OnQueryFileNameHandler;

            _logger.Debug($"Created ProjFS callback handler (detailed trace: {_options.EnableDetailedTrace})");
        }

        public void Dispose()
        {
            foreach (WriteBuffer writeBuffer in _writeBufferPool)
            {
                writeBuffer.Dispose();
            }

            _writeBufferPool.Clear();
        }

        public HResult StartDirectoryEnumerationCallback(
            int commandId,
            Guid enumerationId,
            string virtualRelativePath,
            uint triggeringProcessId,
            string triggeringProcessImageFileName)
        {
            _stats.StartEnumCalled();

            if (_ct.IsCancellationRequested)
            {
                _logger.Debug($"Cancellation token already canceled starting dir enum for '{virtualRelativePath}', returning canceled response");
                return HResult.VirtualizationUnavaliable;
            }

            try
            {
                virtualRelativePath = virtualRelativePath.TrimEnd(Path.DirectorySeparatorChar);
                if (_callbacks.TryGetDirectoryMetadataCacheEntry(virtualRelativePath, out IDirectoryMetadata? dirMetadata))
                {
                    // Note that if metadata indicates the dir does not exist we still return HResult.Ok and
                    // let the enumeration grab zero files from the Entries collection. Otherwise for directories
                    // that are outputs and which were initially predicted or dynamically retrieved as Exists=false
                    // we can have trouble enumerating output files after the command completes.
                    if (_options.EnableDetailedTrace)
                    {
                        _logger.Finest($"StartDirEnum for '{virtualRelativePath}' started by {triggeringProcessImageFileName} (PID {triggeringProcessId}) retrieved from cache");
                    }

                    _currentDirectoryEnumerations[enumerationId] = new DirectoryEnumeration(virtualRelativePath, dirMetadata);
                    return HResult.Ok;
                }

                if (_options.EnableDetailedTrace)
                {
                    _logger.Finest($"StartDirEnum for '{virtualRelativePath}' started by {triggeringProcessImageFileName} (PID {triggeringProcessId}) failed to find directory in cache, starting async callback and returning Pending");
                }

                GetDirectoryFromClient(virtualRelativePath, commandId,
                    newMetadata =>
                    {
                        if (newMetadata.Exists)
                        {
                            _currentDirectoryEnumerations[enumerationId] = new DirectoryEnumeration(virtualRelativePath, newMetadata);
                            return HResult.Ok;
                        }

                        if (_options.EnableDetailedTrace)
                        {
                            _logger.Finest($"Retrieved directory metadata from client for '{virtualRelativePath}', directory does not exist so returning PathNotFound from StartDirectoryEnumeration");
                        }

                        return HResult.PathNotFound;
                    });
                return HResult.Pending;
            }
            catch (Exception ex)
            {
                // Blow up the overall session by informing the caller. Return Pending to avoid providing
                // a failure error code to the calling process.
                SignalFatalError(
                    $"StartDirectoryEnumeration for '{virtualRelativePath}' started by {triggeringProcessImageFileName} encountered a general exception",
                    ex);
                return HResult.Pending;
            }
        }

        /// <summary>
        /// Called from a callback context to log a fatal error and inform the session owner.
        /// </summary>
        private void SignalFatalError(string message, Exception ex)
        {
            _logger.Error(ex, message);
            _callbacks.FatalErrorOccurred(new VirtualFilesystemException(message, ex));
        }

        public HResult GetDirectoryEnumerationCallback(
            int commandId,
            Guid enumerationId,
            string filterFileName,
            [MarshalAs(UnmanagedType.U1)] bool restartScan,
            IDirectoryEnumerationResults result)
        {
            _stats.GetEnumCalled();
            if (!_currentDirectoryEnumerations.TryGetValue(enumerationId, out DirectoryEnumeration? dirInfo))
            {
                _logger.Error($"GetDirEnum enumID {enumerationId} not found in current enum list, returning InvalidArg");
                return HResult.InvalidArg;
            }

            if (_ct.IsCancellationRequested)
            {
                _logger.Debug("Cancellation token already canceled continuing dir enum, returning canceled response");
                return HResult.VirtualizationUnavaliable;
            }

            if (!restartScan)
            {
                if (dirInfo.SetFilter(filterFileName))
                {
                    _callbacks.OnDirectoryEnumeration(dirInfo.VirtualRelativePath, dirInfo.Filter);
                }
            }
            else
            {
                dirInfo.ResetEnumeration(filterFileName);
                _callbacks.OnDirectoryEnumeration(dirInfo.VirtualRelativePath, dirInfo.Filter);
            }

            int numEntriesAdded = 0;
            List<IFileSystemEntry>? entriesAdded = null;

            if (_options.EnableDetailedTrace)
            {
                entriesAdded = new List<IFileSystemEntry>();
            }

            var hr = HResult.Ok;
            do
            {
                IFileSystemEntry? entry = dirInfo.GetCurrentMatchingOrNull();
                if (entry is null)
                {
                    break;
                }

                DateTime lastUpdateTime = entry.LastUpdateTimeUtc ?? _sessionStartTimeUtc;
                bool added = result.Add(
                    entry.Name,
                    entry.FileSize,
                    entry.IsDirectory,
                    entry.IsDirectory ? FileAttributes.Directory : FileAttributes.Archive,
                    lastUpdateTime,
                    lastUpdateTime,
                    lastUpdateTime,
                    lastUpdateTime);
                if (added)
                {
                    numEntriesAdded++;
                    entriesAdded?.Add(entry);
                }
                else
                {
                    if (numEntriesAdded == 0)
                    {
                        hr = HResult.InsufficientBuffer;  // Per documentation
                    }

                    break;
                }
            }
            while (dirInfo.MoveNext());

            if (_options.EnableDetailedTrace)
            {
                _logger.Finest($"GetDirEnum '{dirInfo.DirectoryMetadata.RelativeBasePath}' restartScan={restartScan} filter='{filterFileName}' added {numEntriesAdded} entries returning hr=0x{$"{hr:X}"} ({hr}), entries: {string.Join(", ", entriesAdded!)}");
            }

            return hr;
        }

        public HResult EndDirectoryEnumerationCallback(Guid enumerationId)
        {
            if (_currentDirectoryEnumerations.TryRemove(enumerationId, out DirectoryEnumeration? dirInfo))
            {
                if (_options.EnableDetailedTrace)
                {
                    _logger.Finest($"EndDirEnum '{dirInfo.DirectoryMetadata.RelativeBasePath}'");
                }

                return HResult.Ok;
            }

            _logger.Error($"EndDirEnum enumID {enumerationId} not found in current enum list, returning InvalidArg");
            return HResult.InvalidArg;
        }

        public HResult GetPlaceholderInfoCallback(
            int commandId,
            string virtualRelativePath,
            uint triggeringProcessId,
            string triggeringProcessImageFileName)
        {
            _stats.GetPlaceholderInfoCalled();

            if (_ct.IsCancellationRequested)
            {
                _logger.Debug($"Cancellation token already canceled getting placeholder for '{virtualRelativePath}', returning canceled response");
                return HResult.VirtualizationUnavaliable;
            }

            try
            {
                virtualRelativePath = virtualRelativePath.TrimEnd(Path.DirectorySeparatorChar);

                string? relativeDirPath = Path.GetDirectoryName(virtualRelativePath);
                if (relativeDirPath is null)
                {
                    _logger.Error($"GetPlaceholderInfo for '{virtualRelativePath}' started by {triggeringProcessImageFileName} (PID {triggeringProcessId}) - Unexpected null getting parent dir from file path, returning FileNotFound");
                    return HResult.FileNotFound;
                }

                if (_callbacks.TryGetDirectoryMetadataCacheEntry(relativeDirPath, out IDirectoryMetadata? dirMetadata))
                {
                    return GetPlaceholderInfoFromMetadata(
                        virtualRelativePath,
                        dirMetadata,
                        triggeringProcessImageFileName,
                        triggeringProcessId);
                }

                if (_options.EnableDetailedTrace)
                {
                    _logger.Finest($"GetPlaceholderInfo for '{virtualRelativePath}' started by {triggeringProcessImageFileName} (PID {triggeringProcessId}) - directory metadata not found in cache, starting async callback and returning Pending");
                }

                GetDirectoryFromClient(relativeDirPath, commandId,
                    newMetadata =>
                    {
                        if (newMetadata.Exists)
                        {
                            return GetPlaceholderInfoFromMetadata(
                                virtualRelativePath,
                                newMetadata,
                                triggeringProcessImageFileName,
                                triggeringProcessId);
                        }

                        if (_options.EnableDetailedTrace)
                        {
                            _logger.Finest($"ProjFS: GetPlaceholderInfo for '{virtualRelativePath}' started by {triggeringProcessImageFileName} (PID {triggeringProcessId}) - file entry could not be found, returning FileNotFound");
                        }

                        return HResult.FileNotFound;
                    });
                return HResult.Pending;
            }
            catch (Exception ex)
            {
                // Blow up the overall session by informing the caller. Return Pending to avoid providing
                // a failure error code to the calling process.
                SignalFatalError($"GetPlaceholderInfo for '{virtualRelativePath}' encountered a general exception", ex);
                return HResult.Pending;
            }
        }

        private HResult GetPlaceholderInfoFromMetadata(
            string virtualRelativePath,
            IDirectoryMetadata dirMetadata,
            string triggeringProcessImageFileName,
            uint triggeringProcessId)
        {
            string fileName = Path.GetFileName(virtualRelativePath);
            IFileSystemEntry? fse = dirMetadata.GetEntryOrNull(fileName);
            if (fse is not null)
            {
                string dataSourceCasedRelativePath = virtualRelativePath;
                if (!virtualRelativePath.EndsWith(fse.Name, StringComparison.Ordinal))
                {
                    // Don't use Path.GetDirectoryPath() to get prefix as it can add a '\' prefix.
                    dataSourceCasedRelativePath = string.Concat(virtualRelativePath.AsSpan(0, virtualRelativePath.Length - fileName.Length), fse.Name);
                }

                if (_options.EnableDetailedTrace)
                {
                    _logger.Finest($"GetPlaceholderInfo for '{dataSourceCasedRelativePath}' started by {triggeringProcessImageFileName} (PID {triggeringProcessId})");
                }

                return CreatePlaceholder(_projFs, dataSourceCasedRelativePath, fse, _sessionStartTimeUtc, _logger);
            }

            if (_options.EnableDetailedTrace)
            {
                _logger.Finest($"GetPlaceholderInfo for '{virtualRelativePath}' started by {triggeringProcessImageFileName} (PID {triggeringProcessId}) - no such file entry found in DirectoryMetadata, entries are: {string.Join(",", dirMetadata.Entries.Select(e => e.Name))}");
            }

            return HResult.FileNotFound;
        }

        public HResult GetFileDataCallback(
            int commandId,
            string virtualRelativePath,
            ulong byteOffset,
            uint length,
            Guid dataStreamId,
            byte[] contentId,
            byte[] providerId,
            uint triggeringProcessId,
            string triggeringProcessImageFileName)
        {
            _stats.GetFileDataCalled();

            virtualRelativePath = virtualRelativePath.TrimEnd(Path.DirectorySeparatorChar);

            if (_options.EnableDetailedTrace)
            {
                _logger.Finest($"GetFileData for '{virtualRelativePath}' started by {triggeringProcessImageFileName} (PID {triggeringProcessId}), length {length}");
            }

            if (_ct.IsCancellationRequested)
            {
                _logger.Debug($"Cancellation token already canceled getting file stream for '{virtualRelativePath}', returning canceled response");
                return HResult.VirtualizationUnavaliable;
            }

            // Perform I/O on the callback thread.
            return MaterializeFileAsync(virtualRelativePath, byteOffset, length, dataStreamId, triggeringProcessImageFileName).GetAwaiter().GetResult();
        }

        private async Task<HResult> MaterializeFileAsync(string virtualRelativePath, ulong byteOffset, uint length, Guid dataStreamId, string triggeringProcessImageFileName)
        {
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                Stream stream = await _callbacks.GetFileAsync(virtualRelativePath, _ct);
                Stream streamToWrite = stream;
                await using (stream)
                {
                    await WriteFileStreamAsync(streamToWrite, length, dataStreamId, _ct);
                }

                if (_options.EnableDetailedTrace)
                {
                    _logger.Finest($"Wrote {length} bytes for '{virtualRelativePath}', byteOffset {byteOffset}, in {sw.Elapsed.TotalMilliseconds} ms");
                }

                _stats.MaterializeFile(sw.Elapsed);
                return HResult.Ok;
            }
            catch (FileNotFoundException e)
            {
                _logger.Debug(e, $"{nameof(FileNotFoundException)} getting file stream for '{virtualRelativePath}'");
                return HResult.FileNotFound;
            }
            catch (OperationCanceledException e)
            {
                // Should occur on cancellation of the session, not a fatal error, force
                // an app failure by completing the I/O.
                _logger.Debug(e, $"Cancellation getting file stream for '{virtualRelativePath}'");
                return HResult.VirtualizationUnavaliable;
            }
            catch (GetFileStreamException streamEx) when (!streamEx.IsFatalError)
            {
                _logger.Debug(streamEx, $"{nameof(GetFileStreamException)} with non-fatal classification getting file stream for '{virtualRelativePath}'");
                return HResult.Handle;
            }
            catch (Exception ex)
            {
                // Blow up the overall session by informing the caller. Return Pending to avoid providing
                // a failure error code to the calling process.
                SignalFatalError($"GetFileData for '{virtualRelativePath}' started by {triggeringProcessImageFileName} encountered a general exception", ex);
                return HResult.Pending;
            }
        }

        private void GetDirectoryFromClient(string virtualRelativePath, int commandId, Func<IDirectoryMetadata, HResult> onRetrieved)
        {
            _asyncOperationsNeedingObservation.Post(Task.Run(
                async () =>
                {
                    try
                    {
                        IDirectoryMetadata newMetadata = await _callbacks.RequestDirectoryMetadataAsync(virtualRelativePath, _ct);

                        if (_options.EnableDetailedTrace)
                        {
                            _logger.Finest($"Retrieved directory metadata from client for '{virtualRelativePath}', Exists={newMetadata.Exists}");
                        }

                        _projFs.CompleteCommand(commandId, onRetrieved(newMetadata));
                    }
                    catch (OperationCanceledException)
                    {
                        // Should occur on cancellation of the session, not a fatal error, force
                        // an app failure by completing the I/O.
                        _logger.Debug($"Cancellation getting directory metadata from client for '{virtualRelativePath}'");
                        _projFs.CompleteCommand(commandId, HResult.VirtualizationUnavaliable);
                    }
                    catch (Exception ex)
                    {
                        // Blow up the overall session by informing the caller. Avoid calling CompleteCommand() to
                        // avoid completing the I/O to the calling process with an error.
                        SignalFatalError($"Failed getting directory metadata from client for '{virtualRelativePath}'", ex);
                    }
                },
                _ct));
        }

        /// <summary>
        /// PrjFltQueryFileNameHandler is called by PrjFlt when a file is being deleted or renamed. It is an optimization so that PrjFlt
        /// can avoid calling Start+Get+End enumeration to check if virtualization is still occurring for a file. This method uses the same
        /// rules for deciding what is projected as the enumeration callbacks.
        /// </summary>
        private HResult OnQueryFileNameHandler(string virtualRelativePath)
        {
            Stopwatch sw = Stopwatch.StartNew();
            _logger.Debug($"QueryFileName for '{virtualRelativePath}'");
            _stats.QueryFileNameCalled();

            try
            {
                ReadOnlySpan<char> parentDir = Path.GetDirectoryName(virtualRelativePath);
                if (parentDir.IsEmpty)
                {
                    if (_options.EnableDetailedTrace)
                    {
                        _logger.Finest($"OnQueryFileName for file '{virtualRelativePath}' in root dir returning Ok (virtualizing underway)");
                    }

                    _stats.GetDirectoryMetadata(sw.Elapsed);
                    return HResult.Ok;
                }

                ReadOnlySpan<char> fileName = Path.GetFileName(virtualRelativePath);
                if (_callbacks.TryGetDirectoryMetadataCacheEntry(parentDir.ToString(), out IDirectoryMetadata? parentDirMetadata))
                {
                    foreach (IFileSystemEntry e in parentDirMetadata.Entries)
                    {
                        if (e.Name.AsSpan().Equals(fileName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (_options.EnableDetailedTrace)
                            {
                                _logger.Finest($"ProjFS: OnQueryFileName for file '{virtualRelativePath}' returning Ok (virtualizing underway)");
                            }

                            _stats.GetDirectoryMetadata(sw.Elapsed);
                            return HResult.Ok;
                        }
                    }
                }

                // Common case - ProjFS often probes file paths that have been written as files.
                // We don't kick off a call to the client here, this is a synchronous callback
                // that has no CommandID for sending an HResult.Pending. The assumption is that
                // interesting directories will have already been retrieved.
                if (_options.EnableDetailedTrace)
                {
                    _logger.Finest($"ProjFS: OnQueryFileName for '{virtualRelativePath}' failed to find dir entry in cache or file in directory metadata, returning FileNotFound");
                }

                _stats.GetDirectoryMetadata(sw.Elapsed);
                return HResult.FileNotFound;
            }
            catch (Exception ex)
            {
                // Blow up the overall session by informing the caller. Return Pending to avoid providing
                // a failure error code to the calling process.
                SignalFatalError($"OnQueryFileName: Failure querying virtual path for '{virtualRelativePath}'", ex);
                return HResult.Pending;
            }
        }

        private WriteBuffer GetWriteBuffer()
        {
            if (_writeBufferPool.TryPop(out WriteBuffer? writeBuffer))
            {
                return writeBuffer;
            }

            IWriteBuffer b = _projFs.CreateWriteBuffer(
                byteOffset: 0,
                FileBufferSize,
                out ulong alignedWriteOffset,
                out uint alignedBufferSize);
            return new WriteBuffer(b, alignedWriteOffset, alignedBufferSize);
        }

        private record WriteBuffer(IWriteBuffer Buffer, ulong AlignedWriteOffset, uint AlignedBufferSize) : IDisposable
        {
            public void Dispose()
            {
                Buffer.Dispose();
            }
        }

        private readonly struct FileWriteBufferHandle(WriteBuffer writeBuffer, Action<WriteBuffer> returnBuffer)
            : IDisposable
        {
            public void Dispose()
            {
                returnBuffer(writeBuffer);
            }

            public IWriteBuffer Buffer => writeBuffer.Buffer;
            public ulong AlignedWriteOffset => writeBuffer.AlignedWriteOffset;
            public uint AlignedBufferSize => writeBuffer.AlignedBufferSize;
        }

        private async ValueTask WriteFileStreamAsync(Stream stream, uint length, Guid streamGuid, CancellationToken cancellationToken)
        {
            using var readBufferHandle = new FileReadBufferHandle();
            using var writeBuffer = new FileWriteBufferHandle(GetWriteBuffer(), b => _writeBufferPool.Push(b));

            byte[] buffer = readBufferHandle.Buffer;
            var remainingData = length;
            ulong alignedWriteOffset = writeBuffer.AlignedWriteOffset;
            while (remainingData > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                uint bytesToCopy = Math.Min(remainingData, writeBuffer.AlignedBufferSize);

                try
                {
                    writeBuffer.Buffer.Stream.Seek(0, SeekOrigin.Begin);
                    await StreamCopyBlockToAsync(stream, writeBuffer.Buffer.Stream, bytesToCopy, buffer, cancellationToken);
                }
                catch (IOException ex)
                {
                    throw new GetFileStreamException($"IOException while copying to unmanaged buffer: {ex.Message}", (HResult)HResultExtensions.HResultFromNtStatus.FileNotAvailable);
                }

                HResult writeResult = _projFs.WriteFileData(streamGuid, writeBuffer.Buffer, alignedWriteOffset, bytesToCopy);
                remainingData -= bytesToCopy;
                alignedWriteOffset += bytesToCopy;

                if (writeResult != HResult.Ok)
                {
                    bool isFatal;
                    if (writeResult == (HResult)HResultExtensions.HResultFromNtStatus.FileClosed)
                    {
                        // StatusFileClosed is expected, and occurs when an application closes a file handle before OnGetFileStream
                        // is complete
                        isFatal = false;

                        if (_options.EnableDetailedTrace)
                        {
                            _logger.Finest("ProjFS: FileClosed result writing file data over ProjFS placeholder, this is expected and can result from a thread race");
                        }
                    }
                    else if (writeResult == HResult.FileNotFound)
                    {
                        // PrjWriteFile may return STATUS_OBJECT_NAME_NOT_FOUND if the stream guid provided is not valid (doesn't exist in the stream table).
                        // For each file expansion, PrjFlt creates a new get stream session with a new stream guid, the session starts at the beginning of the 
                        // file expansion, and ends after the GetFileStream command returns or times out.
                        //
                        // If we hit this, the most common explanation is that we're calling PrjWriteFile after the PrjFlt thread waiting on the response
                        // from GetFileStream has already timed out
                        isFatal = false;

                        if (_options.EnableDetailedTrace)
                        {
                            _logger.Finest("ProjFS: FileNotFound result writing file data over ProjFS placeholder, this is expected and can result from a thread race");
                        }
                    }
                    else if (writeResult == HResult.Handle)
                    {
                        isFatal = false;
                        _logger.Debug("InvalidHandle result writing file data over ProjFS placeholder, this is expected when the file is closed before we can write to it");
                    }
                    else
                    {
                        isFatal = true;
                        _logger.Error($"HResult 0x{$"{writeResult:X}"} writing file data over ProjFS placeholder");
                    }

                    throw new GetFileStreamException(writeResult, isFatal);
                }
            }
        }

        private static async ValueTask StreamCopyBlockToAsync(Stream input, Stream destination, long numBytes, byte[] buffer, CancellationToken ct)
        {
            while (numBytes > 0)
            {
                var bytesToRead = Math.Min(buffer.Length, (int)numBytes);
                int bytesRead = await input.ReadAsync(buffer.AsMemory(0, bytesToRead), ct);
                if (bytesRead <= 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                numBytes -= bytesRead;
            }
        }
    }

    private static class HResultExtensions
    {
        private const int FacilityNtBit = 0x10000000; // FACILITY_NT_BIT

        // #define HRESULT_FROM_NT(x)      ((HRESULT) ((x) | FACILITY_NT_BIT))
        public enum HResultFromNtStatus
        {
            FileNotAvailable = unchecked((int)0xC0000467) | FacilityNtBit,       // STATUS_FILE_NOT_AVAILABLE
            FileClosed = unchecked((int)0xC0000128) | FacilityNtBit,             // STATUS_FILE_CLOSED
        }
    }
}
