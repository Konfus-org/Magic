using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading.Channels;
using Magic.Utils;

namespace Magic.Debugging;

/// <summary>
/// The terminal Magic runs in. Standard output holds only what a command gives back, such as <c>get</c>'s values or a
/// screenshot's path, for the person or the script that ran Magic; the log, the progress bars and the prompt go to
/// standard error, so piping one never mixes it with the other. Any thread.
/// </summary>
[SuppressMessage(
    "ApiDesign",
    "RS0030:Do not use banned APIs",
    Justification = "The one place that writes to the console and reads from it."
)]
public static class Terminal
{
    /// <summary>
    /// What stands before the line a person types.
    /// </summary>
    private const string Prompt = "(∩^o^)⊃━☆ ";

    /// <summary>
    /// A progress bar at its full width; a load that is part way shows that part of it.
    /// </summary>
    private const string FullBar = "✩₊˚.⋆☾⋆⁺₊✧✩₊˚.⋆☾⋆⁺₊✧";

    /// <summary>
    /// Clears the line the cursor is on, which a bar or the prompt may hold, and returns to its start.
    /// </summary>
    private const string ClearLine = "\e[2K\r";

    /// <summary>
    /// Guards what a person sees: the line being typed, and the writes to standard error that draw it again.
    /// </summary>
    private static readonly Lock _screen = new();

    /// <summary>
    /// The line a person has typed so far, drawn again after the prompt under whatever is written meanwhile.
    /// </summary>
    private static readonly StringBuilder _typing = new();

    /// <summary>
    /// Sets the console to UTF-8, so the stars and the prompt show rather than question marks, greets a person at the
    /// keyboard, and starts the thread that reads the typed lines. A constructor, so the runtime has run it before the
    /// first line is written, which a property's initializer alone doesn't promise.
    /// </summary>
    static Terminal()
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!Console.IsInputRedirected)
            Console.Error.Write($"Welcome to the magic CLI, type 'help' for commands.\n{Prompt}");

        Channel<string> lines = Channel.CreateUnbounded<string>(new() { SingleReader = true, SingleWriter = true });
        Thread reader = new(() => ReadTyped(lines.Writer)) { IsBackground = true, Name = "CLI Reader" };
        reader.Start();
        Typed = lines.Reader;
    }

    /// <summary>
    /// Draws how far a load is as a bar of stars on standard error, in place, each report over the last; nothing when
    /// standard error is piped.
    /// </summary>
    public static IProgress<LoadProgress> Progress { get; } = new Bar();

    /// <summary>
    /// The lines typed into the terminal, read by one thread for the whole process.
    /// </summary>
    internal static ChannelReader<string> Typed { get; }

    /// <summary>
    /// Writes <paramref name="line"/>, a command's result, to standard output, over any bar or prompt on the screen,
    /// and the prompt and the line being typed again below it when a person is typing.
    /// </summary>
    public static void Write(string line)
    {
        if (Console.IsErrorRedirected)
        {
            Console.Out.WriteLine(line);

            return;
        }

        lock (_screen)
        {
            Console.Error.Write(ClearLine);
            Console.Out.WriteLine(line);
            Console.Error.Write(GetPromptLine());
        }
    }

    /// <summary>
    /// Writes a logged line to standard error, coloured by its level when a person is reading it, over any bar or
    /// prompt, and the prompt and the line being typed again below it when a person is typing.
    /// </summary>
    internal static void Write(in Log.Line line)
    {
        string text = line.ToString();
        if (Console.IsErrorRedirected)
        {
            Console.Error.WriteLine(text);

            return;
        }

        lock (_screen)
            Console.Error.Write($"{ClearLine}{Colorize(line.Level, text)}\n{GetPromptLine()}");
    }

    /// <summary>
    /// Hands <paramref name="lines"/> each line typed, until the input ends. Text piped in is read a line at a time.
    /// A person's keys are read one by one and echoed here, so the line being typed is known and can be drawn again.
    /// </summary>
    private static void ReadTyped(ChannelWriter<string> lines)
    {
        if (Console.IsInputRedirected)
        {
            // Text piped in can start with a byte order mark.
            while (Console.ReadLine() is { } line)
                lines.TryWrite(line.TrimStart('\uFEFF'));
            lines.Complete();

            return;
        }

        // A keyboard never ends: the thread is a background one, which the process takes with it.
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            lock (_screen)
            {
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.Error.Write("\n" + Prompt);
                    lines.TryWrite(_typing.ToString());
                    _typing.Clear();
                }
                else if (key.Key == ConsoleKey.Backspace && _typing.Length > 0)
                {
                    _typing.Length--;
                    Console.Error.Write("\b \b");
                }
                else if (key.KeyChar >= ' ')
                {
                    _typing.Append(key.KeyChar);
                    Console.Error.Write(key.KeyChar);
                }
            }
        }
    }

    /// <summary>
    /// The prompt and the line being typed, for under what was just written; nothing when the lines are piped in.
    /// </summary>
    private static string GetPromptLine() => Console.IsInputRedirected ? string.Empty : $"{Prompt}{_typing}";

    private static string Colorize(Log.LoggingLevel level, string text) =>
        level switch
        {
            Log.LoggingLevel.Verbose => $"\e[90m{text}\e[0m", // gray
            Log.LoggingLevel.Info => text, // default
            Log.LoggingLevel.Warn => $"\e[33m{text}\e[0m", // yellow
            Log.LoggingLevel.Error => $"\e[31m{text}\e[0m", // red
            Log.LoggingLevel.Critical => $"\e[1;97;41m{text}\e[0m", // bold white on red
            _ => text,
        };

    /// <summary>
    /// A load's progress as <c>[✩₊˚.⋆☾⋆⁺₊✧        ] Loading Models 3 of 9</c>, the stars as far along the bar as the
    /// load is.
    /// </summary>
    private sealed class Bar : IProgress<LoadProgress>
    {
        public void Report(LoadProgress value)
        {
            if (Console.IsErrorRedirected)
                return;

            int width = FullBar.Length;
            int filled = value.Total == 0 ? width : Math.Clamp(width * value.Done / value.Total, 0, width);
            lock (_screen)
            {
                Console.Error.Write(
                    $"{ClearLine}[{FullBar[..filled].PadRight(width)}] {value.Work} {value.Done} of {value.Total}"
                );
            }
        }
    }
}
