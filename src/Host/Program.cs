using System.CommandLine;
using System.Data;
using System.Linq;
using System.Runtime;
using Magic.Debugging;

namespace Magic.Host;

internal static class Program
{
    // TODO(KONFUS): Implement in order: Args, Result, Debugging, Crash, IFileSystem, Json, Project, Engine.Run. See the plan below.
    //
    // 1. DONE: Program and Args: get --help working first. Add System.CommandLine to the project (version is already pinned
    //    in Directory.Packages.props, so the reference needs no version). Make an Args record with the nine options as
    //    init properties. In Main, build a RootCommand with nine Option<T>s, call Parse(args), and if the parse result
    //    has errors print them and return 1. Do not let System.CommandLine’s own “invoke” pipeline run the engine for
    //    you. You parse, then you call Engine.Run yourself, so the engine starts on the process’s first thread. Done
    //    when dotnet run -- --help prints the options, --version prints a version, --frames abc exits 1 with a message.
    //
    // 2. DONE: Result. A tiny readonly record struct Result<T> with Ok, Value and Error, two static constructors, and
    //    [MemberNotNullWhen(true, nameof(Value))] on Ok. That attribute is the whole trick: after if (!r.Ok) return
    //    ...; the compiler knows r.Value is not null, so you never write ! or ?. Also a non-generic Result for calls
    //    that return nothing. This is where the System.Diagnostics.CodeAnalysis using belongs, not in Program. Done
    //    when a two-line test in Main compiles without a warning.
    //
    // 3. DONE: The log. A static class Debugging with Verbose, Info, Warn, Error, Critical. Rule from the plan: a log call
    //    never waits on the terminal. The shape is a Channel<LogLine> any thread writes to, and one background thread
    //    that reads it and writes to the console. Count errors with Interlocked.Increment. Flush waits for the channel
    //    to drain, called once at the end of Main. Note that BannedSymbols.txt bans System.Console everywhere, so the
    //    log’s one console call needs a #pragma warning disable RS0030 around that line. Done when two threads logging
    //    at once produce clean lines, and Error count comes back right.
    //
    // 4. DONE: Critical and the crash file. Critical writes the unprinted lines and the stack to Logs/<time>.crash beside the
    //    executable, prints the path, calls Debugger.Break() if attached, then ends the process. In Main, before
    //    anything else, hook AppDomain.CurrentDomain.UnhandledException and TaskScheduler.UnobservedTaskException to
    //    call Critical. Set GCSettings.LatencyMode here too. Done when check 5 of the checkpoint passes.
    //
    // 5. IFileSystem and FileSystem. The interface is the only thing that touches disk, which is why the banned list
    //    refuses File, Directory and Path.Combine everywhere else. The implementing class needs the same pragma as the
    //    log. Methods for step one: Exists, Enumerate (files in a folder by pattern), Combine, Parent, a synchronous
    //    Read for the project file, and ReadAsync(path, progress, cancel) in the full convention even though nothing
    //    calls it yet. Done when it can list the .magic files in a folder.
    //
    // 6. Json and Project. Json is a static class with one JsonSerializerOptions and Read<T>(bytes) returning
    //    Result<T>. The dialect is all options you set, no code: camelCase naming policy, case-insensitive off,
    //    UnmappedMemberHandling.Disallow, JsonStringEnumConverter with JsonNamingPolicy.SnakeCaseLower,
    //    ReadCommentHandling.Skip, AllowTrailingCommas, RespectNullableAnnotations and
    //    RespectRequiredConstructorParameters. Catch JsonException and turn it into the error string. Utf8JsonReader
    //    skips the byte order mark on its own when you deserialize from bytes, which is why Read works on bytes, not a
    //    string. Project is a record with required string Name and required ImmutableArray<string> Gems. Done when
    //    "gemz" fails with a reason and the valid file parses.
    //
    // 7. Engine.Run. Result in, Result out, three private steps: FindProject (a file, or a folder holding exactly one
    //    .magic), ReadProject (read bytes, parse), then log Running <name> from <folder>. Each step returns early on
    //    failure. Program maps the result to 0 or 1, and to 2 when --fail-on-error and the error count is above zero.

    private static int Main(string[] args)
    {
        GCSettings.LatencyMode = GCLatencyMode.LowLatency;

        // An exception nothing caught, on any thread, crashes, leaving a crash file.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Assert.Now($"Unhandled exception: {e.ExceptionObject}");

        using CancellationTokenSource cts = new();

        // Setup commands
        RootCommand cmds = new("Sample command-line app")
        {
            Options =
            {
                new Option<string>("--project", "-p") { Description = "Path to the project file" },
                new Option<bool>("--headless", "-H") { Description = "Run without a GUI (headless mode)" },
                new Option<string>("--verbosity", "-v")
                {
                    Description = "Verbosity level (quiet, minimal, normal, detailed, diagnostic)",
                    Action = new OptionAction(
                        (result) =>
                        {
                            Log.Verbosity = result.GetValue<string>("--verbosity") switch
                            {
                                "quiet" => Log.VerbosityLevel.Quiet,
                                "minimal" => Log.VerbosityLevel.Minimal,
                                "normal" => Log.VerbosityLevel.Normal,
                                "detailed" => Log.VerbosityLevel.Detailed,
                                "diagnostic" => Log.VerbosityLevel.Diagnostic,
                                _ => Log.VerbosityLevel.Normal,
                            };
                        }
                    ),
                },
                new Option<bool>("--exit", "-e")
                {
                    Description = "Tell app to close",
                    Action = new OptionAction(
                        (result) =>
                        {
                            Log.Info("Exiting with code 0.");
                            cts.Cancel();
                        }
                    ),
                },
                new Option<bool>("--exit-now", "-E")
                {
                    Description = "Tell app to close immediately",
                    Action = new OptionAction(
                        (result) =>
                        {
                            Log.Info("Exiting immediately with code 0.");
                            Environment.Exit(0);
                        }
                    ),
                },
                new Option<bool>("--fail-on-error")
                {
                    Description = "Exit with code 2 if any errors were logged",
                    Action = new OptionAction(
                        (result) =>
                        {
                            if (Log.ErrorCount <= 0)
                                return;

                            Log.Critical($"Exiting with code 2 due to logged errors. Error count: {Log.ErrorCount}");
                            Environment.Exit(2);
                        }
                    ),
                },
            },
        };

        // Parse the command-line arguments
        var result = cmds.Parse(args);
        Log.Info($"Command line args: {string.Join(", ", args)}");
        if (result.Errors.Count > 0)
        {
            string errors = string.Join("; ", result.Errors.Select(e => e.Message));
            Log.Error($"Failed to parse command-line arguments: {errors}");
            return 1;
        }

        // Extract data from give args
        string? projectPath = result.GetValue<string>("--project");
        if (string.IsNullOrEmpty(projectPath))
        {
            Log.Error("No project path specified. Use --project <path> to specify a project.");
            return 1;
        }

        Log.Info($"Opening: {projectPath}");
        // TODO(KONFUS): Project.Open(projectPath);
        Log.Info("Successfully opened project.");

        bool headless = result.GetValue<bool>("--headless");
        if (headless)
        {
            Log.Info("Running in headless mode.");
            Cli.Run(cts, cmds);
        }
        else
        {
            Log.Info("Running in GUI mode.");
            // TODO(KONFUS): Gui.Run(cts, cmds);
        }

        Log.Flush();

        return 0;
    }
}
