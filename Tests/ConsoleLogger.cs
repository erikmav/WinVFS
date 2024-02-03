using System;
using WinVfs.VirtualFilesystem;

namespace WinVfs.Tests;

internal sealed class ConsoleLogger : ILogger
{
    public bool IsFinestLoggingEnabled { get; set; }

    public void Debug(ref LogInterpolatedStringHandler message)
    {
        Console.WriteLine(message.GetFormattedText());
    }

    public void Debug(Exception exception, ref LogInterpolatedStringHandler message)
    {
        Console.WriteLine(message.GetFormattedText() + Environment.NewLine + exception);
    }

    public void Debug(string message)
    {
        Console.WriteLine(message);
    }

    public void Error(string message)
    {
        Console.WriteLine($"ERROR: {message}");
    }

    public void Error(Exception exception, string message)
    {
        Console.WriteLine($"ERROR: {message}{Environment.NewLine}{exception}");
    }

    public void Error(ref LogInterpolatedStringHandler message)
    {
        Console.WriteLine($"ERROR: {message.GetFormattedText()}");
    }

    public void Finest(ref LogInterpolatedStringHandler message)
    {
        if (IsFinestLoggingEnabled)
        {
            Console.WriteLine(message.GetFormattedText());
        }
    }

    public void Finest(string message)
    {
        if (IsFinestLoggingEnabled)
        {
            Console.WriteLine(message);
        }
    }
}
