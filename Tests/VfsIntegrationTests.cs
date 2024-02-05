using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinVfs.VirtualFilesystem;

namespace WinVfs.Tests;

[TestClass]
public sealed class VfsIntegrationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EnumerateVirtRootAndReadFileAndSubDir(bool returnDirsInCacheLookup)
    {
        using var tempDir = new DisposableTempDirectory();
        string virtRoot = Path.Combine(tempDir.Path, "virt");
        Directory.CreateDirectory(virtRoot);

        var logger = new ConsoleLogger { IsFinestLoggingEnabled = true };

        var callbacks = new MockVfsCallbacks { ReturnDirectoriesInCacheLookup = returnDirsInCacheLookup };

        var rootDirMetadata = new DirectoryMetadata(string.Empty);
        rootDirMetadata.WritableEntries.Add(new FileSystemEntry("file", 1234));
        rootDirMetadata.WritableEntries.Add(new FileSystemEntry("subdir"));
        callbacks.Filesystem[rootDirMetadata.RelativeBasePath] = rootDirMetadata;
        string filePath = Path.Combine(virtRoot, "file");
        callbacks.FileSizes["file"] = 1234;

        DateTime hourAgo = DateTime.UtcNow - TimeSpan.FromHours(1);
        var subdirMetadata = new DirectoryMetadata("subdir");
        subdirMetadata.WritableEntries.Add(new FileSystemEntry("subdirFile", 5678, hourAgo));
        callbacks.Filesystem[subdirMetadata.RelativeBasePath] = subdirMetadata;

        DateTime sessionStart = DateTime.UtcNow - TimeSpan.FromMinutes(1);

        using var projFsSession = new ProjFsVirtualizationSession(
            virtRoot,
            callbacks,
            logger,
            sessionStart,
            new ProjFsOptions { EnableDetailedTrace = true },
            CancellationToken.None);

        // Enumerate and verify the contents of the virtualization root.
        var virtRootDir = new DirectoryInfo(virtRoot);
        FileSystemInfo[] entries = virtRootDir.GetFileSystemInfos();
        Assert.AreEqual(2, entries.Length);
        Assert.AreEqual("file", entries[0].Name);
        Assert.IsFalse(entries[0].Attributes.HasFlag(FileAttributes.Directory));
        Assert.AreEqual(1234L, ((FileInfo)entries[0]).Length);
        Assert.AreEqual(sessionStart, entries[0].LastWriteTimeUtc);
        Assert.AreEqual(sessionStart, entries[0].CreationTimeUtc);
        Assert.AreEqual(sessionStart, entries[0].LastAccessTimeUtc);
        Assert.AreEqual("subdir", entries[1].Name);
        Assert.IsTrue(entries[1].Attributes.HasFlag(FileAttributes.Directory));

        Assert.AreEqual(1, callbacks.NumTryGetDirectoryMetadataCacheEntryCalls);
        Assert.AreEqual(returnDirsInCacheLookup ? 0 : 1, callbacks.NumRequestDirectoryMetadataCalls);
        Assert.AreEqual(1, callbacks.NumDirectoryEnumerations);
        Assert.AreEqual(0, callbacks.NumGetFileCalls);
        Assert.AreEqual(0, callbacks.NumFatalErrors);

        // Read the contents of the file in the root.
        byte[] fileContents = File.ReadAllBytes(filePath);
        Assert.AreEqual(1234, fileContents.Length);
        byte expectedContent = 0x01;
        for (int i = 0; i < fileContents.Length; i++)
        {
            Assert.AreEqual(expectedContent, fileContents[i]);
            if (expectedContent == 0xFF)
            {
                expectedContent = 0x00;
            }
            else
            {
                expectedContent++;
            }
        }

        Assert.AreEqual(2, callbacks.NumTryGetDirectoryMetadataCacheEntryCalls);
        Assert.AreEqual(returnDirsInCacheLookup ? 0 : 2, callbacks.NumRequestDirectoryMetadataCalls);
        Assert.AreEqual(1, callbacks.NumDirectoryEnumerations);
        Assert.AreEqual(1, callbacks.NumGetFileCalls);
        Assert.AreEqual(0, callbacks.NumFatalErrors);

        // Enumerate and verify the contents of subdir.
        string subdirPath = Path.Combine(virtRoot, "subdir");
        var subdirDir = new DirectoryInfo(subdirPath);
        entries = subdirDir.GetFileSystemInfos();
        Assert.AreEqual(1, entries.Length);
        Assert.AreEqual("subdirFile", entries[0].Name);
        Assert.IsFalse(entries[0].Attributes.HasFlag(FileAttributes.Directory));
        Assert.AreEqual(5678, ((FileInfo)entries[0]).Length);
        Assert.AreEqual(hourAgo, entries[0].LastWriteTimeUtc);
        Assert.AreEqual(hourAgo, entries[0].CreationTimeUtc);
        Assert.AreEqual(hourAgo, entries[0].LastAccessTimeUtc);

        Assert.AreEqual(4, callbacks.NumTryGetDirectoryMetadataCacheEntryCalls);
        Assert.AreEqual(returnDirsInCacheLookup ? 0 : 4, callbacks.NumRequestDirectoryMetadataCalls);
        Assert.AreEqual(2,callbacks.NumDirectoryEnumerations);
        Assert.AreEqual(1, callbacks.NumGetFileCalls);
        Assert.AreEqual(0, callbacks.NumFatalErrors);
    }
}

internal sealed class MockVfsCallbacks : IVfsCallbacks
{
    /// <summary>
    /// Whether directories in <see cref="Filesystem"/> are returned in <see cref="TryGetDirectoryMetadataCacheEntry"/>.
    /// </summary>
    public bool ReturnDirectoriesInCacheLookup { get; set; }

    public Dictionary<string, IDirectoryMetadata> Filesystem { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> FileSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int NumTryGetDirectoryMetadataCacheEntryCalls;
    public bool TryGetDirectoryMetadataCacheEntry(string virtualRelativePath, [NotNullWhen(true)] out IDirectoryMetadata? directoryMetadata)
    {
        Interlocked.Increment(ref NumTryGetDirectoryMetadataCacheEntryCalls);

        if (!ReturnDirectoriesInCacheLookup)
        {
            directoryMetadata = null;
            return false;
        }

        return Filesystem.TryGetValue(virtualRelativePath, out directoryMetadata);
    }

    public int NumRequestDirectoryMetadataCalls;
    public ValueTask<IDirectoryMetadata> RequestDirectoryMetadataAsync(string virtualRelativePath, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref NumRequestDirectoryMetadataCalls);

        if (Filesystem.TryGetValue(virtualRelativePath, out IDirectoryMetadata? dirMetadata))
        {
            return ValueTask.FromResult(dirMetadata);
        }

        return ValueTask.FromResult<IDirectoryMetadata>(new DirectoryMetadata(virtualRelativePath, exists: false));
    }

    public int NumDirectoryEnumerations;
    public void OnDirectoryEnumeration(string virtualRelativePath, string enumerationPattern)
    {
        Interlocked.Increment(ref NumDirectoryEnumerations);
    }

    public int NumGetFileCalls;
    public ValueTask<Stream> GetFileAsync(string virtualRelativePath, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref NumGetFileCalls);
        if (FileSizes.TryGetValue(virtualRelativePath, out int size))
        {
            var contents = new byte[size];
            byte content = 0x01;
            for (int i = 0; i < size; i++)
            {
                contents[i] = content;
                if (content == 0xFF)
                {
                    content = 0x00;
                }
                else
                {
                    content++;
                }
            }
            
            return ValueTask.FromResult<Stream>(new MemoryStream(contents));
        }

        throw new FileNotFoundException(virtualRelativePath);
    }

    public int NumFatalErrors;
    public void FatalErrorOccurred(Exception exception)
    {
        Interlocked.Increment(ref NumFatalErrors);
    }
}
