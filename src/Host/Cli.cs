using System.CommandLine;
using System.Threading.Channels;
using Magic.Debugging;

namespace Magic.Host;

internal static class Cli
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "ApiDesign",
        "RS0030:Do not use banned APIs",
        Justification = "Must read console for CLI"
    )]
    public static void Run(CancellationTokenSource cts, RootCommand cmdLineOptions)
    {
        Channel<string> cli = Channel.CreateUnbounded<string>(new() { SingleReader = true, SingleWriter = true });
        Thread reader = new(() =>
        {
            while (Console.ReadLine() is { } line)
                cli.Writer.TryWrite(line);
            cli.Writer.Complete();
        })
        {
            IsBackground = true,
            Name = "CLI Reader",
        };
        reader.Start();

        Log.Info("Welcome to the magic CLI, type 'help' for options.");

        for (int frameIndex = 0; !cts.Token.IsCancellationRequested; frameIndex++)
        {
            while (cli.Reader.TryRead(out string? line))
            {
                if (line.Length > 1 && line[0] != '-' && line[1] != '-')
                    line = "--" + line; // Append -- for commands for user
                ParseResult command = cmdLineOptions.Parse(line);
                if (command.Errors.Count > 0)
                {
                    Log.Error($"Failed to parse command: {command.Errors}");
                    continue;
                }

                command.Invoke();
            }
        }
    }
}
