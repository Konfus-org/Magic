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
    public static void Run(CancellationTokenSource cts, RootCommand cmds)
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

        for (int frameIndex = 0; !cts.Token.IsCancellationRequested; frameIndex++)
        {
            while (cli.Reader.TryRead(out string? line))
            {
                ParseResult command = cmds.Parse(line);
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
