using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Magic.Services;

namespace Magic.Extensions;

/// <summary>
/// Letting a task go without awaiting it, so that what it throws is still logged rather than lost.
/// </summary>
public static class TaskExtensions
{
    extension(Task task)
    {
        /// <summary>
        /// Lets the task run on with nothing awaiting it. If it fails, what it threw is a
        /// <see cref="Debugging.Error"/> logged from where it was let go; a cancelled task has not failed.
        /// </summary>
        [SuppressMessage("Design", "CA1030:Use events where appropriate", Justification = "It lets a task go; nothing is raised.")]
        public void FireAndForget([CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            task.ConfigureAwait(false).GetAwaiter().OnCompleted(() =>
            {
                if (task.Exception is { } failure)
                {
                    Exception thrown = failure.InnerExceptions.Count == 1 ? failure.InnerExceptions[0] : failure;
                    Debugging.Error($"A task let go with FireAndForget failed: {thrown}", member, file, line);
                }
            });
        }
    }
}
