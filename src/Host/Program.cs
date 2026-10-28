using System.CommandLine;
using System.Data;
using System.Linq;
using System.Runtime;
using Magic.Debugging;
using Magic.Files;
using Magic.Projects;
using Magic.State;

namespace Magic.Host;

internal static class Program
{
    private static int Main(string[] args)
    {
        Log.Info($"Command line args: {string.Join(", ", args)}");

        GCSettings.LatencyMode = GCLatencyMode.LowLatency;

        // An exception nothing caught, on any thread, crashes, leaving a crash file.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Assert.Now($"Unhandled exception: {e.ExceptionObject}");

        using CancellationTokenSource cts = new(); // Kills app when cancelled
        IFileSystem fileSystem = new FileSystem();

        RootCommand cmdLineOptions = CreateCmdLineOptions(cts, fileSystem);
        var cmdLineOptionsParseResult = ParseAndInvokeCmdLineOptions(args, cmdLineOptions);
        if (!cmdLineOptionsParseResult.Ok)
        {
            Log.Critical(cmdLineOptionsParseResult.Error);
            return 1;
        }

        Log.Flush();
        return 0;
    }

    private static RootCommand CreateCmdLineOptions(CancellationTokenSource cts, IFileSystem fileSystem)
    {
        Project? project = null;
        bool isHeadless = false;

        RootCommand cmdLineOptions = new("Sample command-line app")
        {
            Options =
            {
                new Option<string>("--project")
                {
                    Description = "Opens a project at the given path",
                    Action = new Command(cmds =>
                    {
                        cts.Cancel(); // close current run

                        // Restart with new project
                        {
                            string? projectPath = cmds.GetValue<string>("--project");
                            if (string.IsNullOrEmpty(projectPath))
                            {
                                Log.Warn("Failed to change projects, path was null.");
                                return;
                            }

                            Log.Info($"Opening: {projectPath}");
                            var loadProjectResult = Project.Load(projectPath, fileSystem);
                            if (!loadProjectResult.Ok)
                            {
                                Log.Warn(loadProjectResult.Error);
                                return;
                            }
                            project = loadProjectResult.Value;
                            Log.Info($"Successfully opened project {project.Value.Name}.");

                            Run(cts, (RootCommand)cmds.RootCommandResult.Command, loadProjectResult.Value, isHeadless);
                        }
                    }),
                },
                new Option<bool>("--headless")
                {
                    Description = "Run without a GUI (headless mode)",
                    Action = new Command(cmds =>
                    {
                        cts.Cancel(); // Close current run

                        var headless = cmds.GetValue<bool>("--headless");
                        if (headless == isHeadless)
                            return; // No change

                        // Restart headless or not headless (whatever the change was)
                        {
                            if (project is null)
                            {
                                Log.Warn(
                                    "Failed to update to headless, no project is active please set one using '--project'."
                                );
                                return;
                            }

                            Run(cts, (RootCommand)cmds.RootCommandResult.Command, project.Value, isHeadless);
                        }
                    }),
                },
                new Option<string>("--verbosity")
                {
                    Description = "Verbosity level (quiet, minimal, normal, detailed, diagnostic)",
                    Action = new Command(cmds =>
                    {
                        Log.Verbosity = cmds.GetValue<string>("--verbosity") switch
                        {
                            "quiet" => Log.VerbosityLevel.Quiet,
                            "minimal" => Log.VerbosityLevel.Minimal,
                            "normal" => Log.VerbosityLevel.Normal,
                            "detailed" => Log.VerbosityLevel.Detailed,
                            "diagnostic" => Log.VerbosityLevel.Diagnostic,
                            _ => Log.VerbosityLevel.Normal,
                        };
                    }),
                },
                new Option<bool>("--exit")
                {
                    Description = "Tell app to close",
                    Action = new Command(_ =>
                    {
                        Log.Info("Exiting with code 0.");
                        cts.Cancel();
                    }),
                },
                new Option<bool>("--exit-now")
                {
                    Description = "Tell app to close immediately",
                    Action = new Command(_ =>
                    {
                        Log.Info("Exiting immediately with code 0.");
                        Environment.Exit(0);
                    }),
                },
                new Option<bool>("--fail-on-error")
                {
                    Description = "Exit with code 2 if any errors were logged",
                    Action = new Command(_ =>
                    {
                        if (Log.ErrorCount <= 0)
                            return;

                        Log.Critical($"Exiting with code 2 due to logged errors. Error count: {Log.ErrorCount}");
                        Environment.Exit(2);
                    }),
                },
            },
        };
        return cmdLineOptions;
    }

    private static Result ParseAndInvokeCmdLineOptions(string[] args, RootCommand cmdLineOptions)
    {
        var cmds = cmdLineOptions.Parse(args);
        if (cmds.Errors.Count > 0)
        {
            string errors = string.Join("; ", cmds.Errors.Select(e => e.Message));
            return Result.Failure($"Failed to parse command-line arguments: {errors}");
        }

        // Run any commands on start
        if (cmds.Invoke() != 0)
        {
            string errors = string.Join("; ", cmds.Errors.Select(e => e.Message));
            return Result.Failure($"Failed to invoke command-line arguments: {errors}");
        }

        return Result.Success();
    }

    private static void Run(CancellationTokenSource cts, RootCommand cmdLineOptions, Project project, bool isHeadless)
    {
        if (isHeadless)
        {
            Log.Info("Running in headless mode.");
            Cli.Run(cts, cmdLineOptions);
        }
        else
        {
            Log.Info("Running in GUI mode.");
            // TODO(KONFUS): Gui.Run(cts, cmds);
        }
    }
}
