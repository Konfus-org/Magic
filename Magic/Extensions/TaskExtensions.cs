using Magic.Utils;
using System.Runtime.CompilerServices;

namespace Magic.Extensions;

public static class TaskExtensions
{
    extension(Task task)
    {
        /// <summary>
        /// Lets the task run without waiting for it, and says so: what it throws is logged as an error (a cancellation
        /// is not), against the line that let it go. The only way to not await a task; one that is simply dropped
        /// does not build (VSTHRD110, CS4014), and one discarded with <c>_ =</c> hides what it threw.
        /// </summary>
        public void FireAndForget([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            if (task.IsCompleted)
            {
                LogFault(task, file, line);
                return;
            }

            task.ConfigureAwait(false).GetAwaiter().OnCompleted(() => LogFault(task, file, line));
        }
    }

    private static void LogFault(Task task, string file, int line)
    {
        if (task.Exception is { } thrown)
            Debugging.Log.Error($"A task left to run on its own threw: {thrown.GetBaseException().Message}", file: file, line: line);
    }
}
