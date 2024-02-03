using System;
using System.Threading;

namespace WinVfs.VirtualFilesystem;

/// <summary>
/// Contains statistics for a <see cref="ProjFsVirtualizationSession"/>.
/// </summary>
public sealed class ProjFsStats
{
    private int _numStartEnum;
    private int _numGetEnum;
    private int _numGetPlaceholderInfo;
    private int _numGetFileData;
    private int _numQueryFileName;
    private int _numGetDirectoryMetadata;
    private int _numMaterializeFile;
    private long _getDirectoryMetadataTicks;
    private long _materializeFileTicks;

    public int NumStartEnumCalls => _numStartEnum;
    public int NumGetEnumCalls => _numGetEnum;
    public int NumGetPlaceholderInfoCalls => _numGetPlaceholderInfo;
    public int NumGetFileDataCalls => _numGetFileData;
    public int NumQueryFileNameCalls => _numQueryFileName;
    public int NumGetDirectoryMetadata => _numGetDirectoryMetadata;
    public int NumMaterializeFile => _numMaterializeFile;
    public TimeSpan GetDirectoryMetadataTime => TimeSpan.FromTicks(_getDirectoryMetadataTicks);
    public TimeSpan MaterializeFileTime => TimeSpan.FromTicks(_materializeFileTicks);

    internal void StartEnumCalled()
    {
        Interlocked.Increment(ref _numStartEnum);
    }

    internal void GetEnumCalled()
    {
        Interlocked.Increment(ref _numGetEnum);
    }

    internal void GetPlaceholderInfoCalled()
    {
        Interlocked.Increment(ref _numGetPlaceholderInfo);
    }

    internal void GetFileDataCalled()
    {
        Interlocked.Increment(ref _numGetFileData);
    }

    internal void QueryFileNameCalled()
    {
        Interlocked.Increment(ref _numQueryFileName);
    }

    internal void GetDirectoryMetadata(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _numGetDirectoryMetadata);
        Interlocked.Add(ref _getDirectoryMetadataTicks, elapsed.Ticks);
    }

    internal void MaterializeFile(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _numMaterializeFile);
        Interlocked.Add(ref _materializeFileTicks, elapsed.Ticks);
    }

    public override string ToString()
    {
        return $"StartEnum={NumStartEnumCalls}" + Environment.NewLine +
               $"GetEnum={NumGetEnumCalls}" + Environment.NewLine +
               $"GetPlaceholderInfo={NumGetPlaceholderInfoCalls}" + Environment.NewLine +
               $"GetFileData={NumGetFileDataCalls}" + Environment.NewLine +
               $"QueryFileName={NumQueryFileNameCalls}" + Environment.NewLine +
               $"GetDirectoryMetadata={NumGetDirectoryMetadata} (Total: {GetDirectoryMetadataTime.TotalMilliseconds}ms)" + Environment.NewLine +
               $"MaterializeFile={NumMaterializeFile} (Total: {MaterializeFileTime.TotalMilliseconds}ms)";
    }
}
