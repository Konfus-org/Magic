using System.ComponentModel;
using System.Diagnostics;
using Magic.State;

namespace Magic.CommandLine;

/// <summary>
/// Runs other programs in Magic's terminal, with a reason instead of an exception when one can't run.
/// </summary>
internal static class Processes
{
    /// <summary>
    /// Runs <paramref name="program"/> with <paramref name="arguments"/>, printing to the terminal, and waits for it to
    /// end. Fails when it can't start or exits with a code other than 0.
    /// </summary>
    public static Result Run(string program, IEnumerable<string> arguments)
    {
        Process? process;
        try
        {
            process = Process.Start(program, arguments);
        }
        catch (Win32Exception exception)
        {
            return Result.Failure($"{program} didn't start: {exception.Message}");
        }

        if (process is null)
            return Result.Failure($"{program} didn't start.");

        using (process)
        {
            // Holds the terminal until the program ends: at launch nothing else runs yet, and typed while a project
            // runs it pauses only the terminal's pump.
            process.WaitForExit();

            return process.ExitCode == 0
                ? Result.Success()
                : Result.Failure($"{program} exited with code {process.ExitCode}.");
        }
    }
}
