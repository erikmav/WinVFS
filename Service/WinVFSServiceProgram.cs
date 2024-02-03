using System;
using System.IO;
using System.Threading;
using CommandLine;
using CommandLine.Text;
using WinVfs.VirtualFilesystem;

namespace WinVfs.Service;

internal sealed class WinVfsServiceProgram
{
    static int Main(string[] args)
    {
        int retCode = (int)ReturnCode.Success;
        try
        {
            using var parser = new Parser(
                settings =>
                {
                    settings.AutoHelp = true;
                    settings.CaseSensitive = false;
                    settings.CaseInsensitiveEnumValues = true;
                });
            ParserResult<CommandLineOptions> parserResult = parser.ParseArguments<CommandLineOptions>(args);
            parserResult
                .WithParsed(opts => retCode = Run(opts))
                .WithNotParsed(_ => retCode = (int)DisplayHelp(parserResult));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unexpected top-level exception: {ex}");
            retCode = (int)ReturnCode.GeneralException;
        }

        return retCode;
    }

    private static int Run(CommandLineOptions opts)
    {
        Console.WriteLine($"Mirroring source dir '{opts.SourceDir}' to '{opts.VirtualizationRoot}");
        Console.WriteLine();
        
        var logger = new ConsoleLogger { IsFinestLoggingEnabled = opts.Verbose };
        var callbacks = new MirroredDirectoryVfsCallbacks(opts.SourceDir);
        Directory.CreateDirectory(opts.VirtualizationRoot);
        using var projFsSession = new ProjFsVirtualizationSession(
            opts.VirtualizationRoot,
            callbacks,
            logger,
            DateTime.UtcNow,
            new ProjFsOptions { EnableDetailedTrace = opts.Verbose },
            CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine("Press Enter to stop virtualization");
        Console.WriteLine();
        Console.ReadLine();
        return 0;
    }

    private static ReturnCode DisplayHelp(ParserResult<CommandLineOptions> parserResult)
    {
        HelpText helpText = HelpText.AutoBuild(parserResult, maxDisplayWidth: ConsoleUtils.GetConsoleWidthForHelpText());
        Console.Error.WriteLine(helpText);
        return ReturnCode.InvalidArguments;
    }

    private enum ReturnCode
    {
        Success = 0,
        InvalidArguments = 1,
        GeneralException = 2,
    }
}

internal sealed class CommandLineOptions
{
    [Option('r', nameof(VirtualizationRoot), Required = true,
        HelpText = "The directory in which to mount the virtual filesystem")]
    public string VirtualizationRoot { get; set; } = string.Empty;

    [Option('s', nameof(SourceDir), Required = true,
        HelpText = "The source directory to mirror into the virtualization root")]
    public string SourceDir { get; set; } = string.Empty;

    [Option('v', nameof(Verbose), Required = false, HelpText = "Output verbose/finest logging level.")]
    public bool Verbose { get; set; }
}
