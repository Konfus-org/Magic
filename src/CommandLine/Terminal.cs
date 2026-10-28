using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Magic.Debugging;

namespace Magic.CommandLine;

/// <summary>
/// The terminal Magic runs in, the one place that reads what is typed into it.
/// </summary>
internal static class Terminal
{
    /// <summary>
    /// The lines typed into the terminal, read by one thread for the whole process.
    /// </summary>
    private static readonly ChannelReader<string> _lines = StartReadingLines();

    /// <summary>
    /// Takes the next line typed into the terminal, if one is waiting.
    /// </summary>
    public static bool TryReadLine([MaybeNullWhen(false)] out string line) => _lines.TryRead(out line);

    [SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "Must read console for CLI")]
    private static ChannelReader<string> StartReadingLines()
    {
        Channel<string> lines = Channel.CreateUnbounded<string>(new() { SingleReader = true, SingleWriter = true });
        Thread reader = new(() =>
        {
            // Text piped in can start with a byte order mark.
            while (Console.ReadLine() is { } line)
                lines.Writer.TryWrite(line.TrimStart('\uFEFF'));
            lines.Writer.Complete();
        })
        {
            IsBackground = true,
            Name = "CLI Reader",
        };
        reader.Start();
        Log.Info("Welcome to the magic CLI, type 'help' for options.");

        return lines.Reader;
    }
}
