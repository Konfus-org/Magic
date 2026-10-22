using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime;
using Magic.Contexts.CmdLine;
using Magic.Services;

namespace Magic;

/// <summary>
/// The Magic executable: reads the command line and runs the engine. Exit codes: 0 clean, 1 bad arguments, or a
/// project that cannot be opened, 2 an error was logged under --fail-on-error. A crash, on any thread, is
/// <see cref="Debugging.Critical"/>: it leaves a crash file and ends the process with the OS's crash code.
/// </summary>
internal static class Program
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A crash goes to Debugging.Critical, which writes a crash file and ends the process.")]
    private static async Task<int> Main(string[] rawArgs)
    {
        AppDomain.CurrentDomain.UnhandledException += static (_, e) => Debugging.Critical(
            $"Magic crashed: {e.ExceptionObject}");

        // Configure default cmd line args and parser.
        Option<string> project = new("--project")
        {
            Description = "The project to run: its .magic file, or the folder holding it.",
            Required = true,
        };
        Option<ulong?> frames = new("--frames")
        {
            Description = "Stop after this many frames, instead of running until a gem stops it.",
        };
        Option<TimeSpan?> seconds = new("--seconds")
        {
            Description = "Stop once the run has gone on this many seconds, however many frames that is: a ceiling "
                + "for a run that should have stopped sooner. A run that must stop at the same point counts --frames.",
            CustomParser = static result =>
            {
                if (!double.TryParse(result.Tokens[0].Value, CultureInfo.InvariantCulture, out double limit)
                    || limit is not > 0
                    || limit >= TimeSpan.MaxValue.TotalSeconds)
                {
                    result.AddError("--seconds takes a number of seconds above 0.");
                    return null;
                }

                return TimeSpan.FromSeconds(limit);
            },
        };
        Option<bool> headless = new("--headless")
        {
            Description = "The run has no window: Ctrl+C stops it cleanly, ending its gems, "
                + "instead of ending the process at once.",
        };
        Option<string[]> settings = new("--set")
        {
            Description = "Overrides a gem setting from the presets, as many times as needed. The value is JSON, "
                + "or a string when it is not valid JSON.",
            HelpName = "Gem.setting=value",
        };
        settings.Validators.Add(static result =>
        {
            foreach (Token token in result.Tokens)
            {
                if (Settings.Parse(token.Value) is { Ok: false } setting) result.AddError(setting.Error);
            }
        });
        Option<string> preset = new("--preset")
        {
            Description
                = "Applies the preset Presets/<name>.preset over the default one, the project's settings, under --set.",
            HelpName = "name",
        };
        Option<bool> failOnError = new("--fail-on-error")
        {
            Description = "Exit with code 2 if any error was logged.",
        };
        Option<bool> bake = new("--bake")
        {
            Description = "Open each of the project's domains in turn, each once the last is shown, then stop: "
                + "everything they use is made and cached, as a Release build bakes it.",
        };
        Option<bool> verbose = new("--verbose")
        {
            Description = "Also log Verbose lines: detail that is usually noise.",
        };
        RootCommand command = new("Magic runs a project and its gems.") {
            project,
            frames,
            seconds,
            headless,
            settings,
            preset,
            failOnError,
            bake,
            verbose };

        // Help, version and bad arguments are answered here and exit.
        ParseResult parsed = command.Parse(rawArgs);
        if (parsed.Action is not null) return await parsed.InvokeAsync().ConfigureAwait(false);

        Debugging.IsVerbose = parsed.GetValue(verbose);

        // A game would rather have the heap grow than have every thread stopped for a full collection mid-frame.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        Args args = new()
        {
            ProjectPath = parsed.GetRequiredValue(project),
            FrameLimit = parsed.GetValue(frames) ?? ulong.MaxValue,
            TimeLimit = parsed.GetValue(seconds) ?? TimeSpan.MaxValue,
            Headless = parsed.GetValue(headless),
            Settings = parsed.GetValue(settings) ?? [],
            Preset = parsed.GetValue(preset) ?? string.Empty,
            Bake = parsed.GetValue(bake),
        };

        // Nothing is awaited before the engine runs, so it runs on the process's first thread, which the OS ties
        // windows and their events to: the render thread.
        try
        {
            if (!Engine.Run(args)) return 1;
        }
        catch (Exception ex)
        {
            Debugging.Critical($"Magic crashed: {ex}");
        }
        finally
        {
            await Debugging.FlushAsync().ConfigureAwait(false);
        }

        return parsed.GetValue(failOnError) && Debugging.ErrorCount > 0 ? 2 : 0;
    }
}
