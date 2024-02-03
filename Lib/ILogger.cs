using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace WinVfs.VirtualFilesystem;

public interface ILogger
{
    /// <summary>
    /// When true, Finest logging is enabled, which will add more CPU and GC load to log detailed information.
    /// </summary>
    public bool IsFinestLoggingEnabled { get; }

    void Debug(ref LogInterpolatedStringHandler message);

    void Debug(Exception exception, ref LogInterpolatedStringHandler message);

    void Debug(string message);

    void Error(string message);

    void Error(Exception exception, string message);

    void Error(ref LogInterpolatedStringHandler message);

    /// <summary>
    /// Most detailed log level, typically turned off by default to avoid CPU and GC load that can affect performance.
    /// </summary>
    void Finest(ref LogInterpolatedStringHandler message);

    void Finest(string message);
}

[InterpolatedStringHandler]
public ref struct LogInterpolatedStringHandler
{
    // Storage for the built-up string
    private readonly int _literalLength;
    private StringBuilder? _builder;

    public LogInterpolatedStringHandler(int literalLength, int formattedCount)
    {
        _literalLength = literalLength;
    }

    public void AppendLiteral(string s)
    {
        _builder ??= new StringBuilder(_literalLength);
        _builder.Append(s);
    }

    public void AppendFormatted<T>(T? t)
    {
        _builder ??= new StringBuilder(_literalLength);
        _builder.Append(t);
    }

    public string GetFormattedText() => _builder?.ToString() ?? string.Empty;
}
