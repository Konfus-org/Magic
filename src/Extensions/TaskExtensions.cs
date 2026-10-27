using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Magic.Debugging;

namespace Magic.Extensions;

public static class TaskExtensions
{
    extension(Task task)
    {
        /// <summary>
        /// Lets <paramref name="task"/> run on without waiting for it. If it faults, the exception is logged as an
        /// error at the call site; if it is cancelled, nothing is.
        /// </summary>
        [SuppressMessage(
            "Design",
            "CA1030:Use events where appropriate",
            Justification = "Fire as in fire and forget, not raising an event."
        )]
        public void FireAndForget(
            [CallerMemberName] string member = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0
        ) =>
            task.ConfigureAwait(false)
                .GetAwaiter()
                .OnCompleted(() =>
                {
                    if (task.Exception is { } fault)
                        Log.Error(
                            $"A fire-and-forget task failed: {fault.InnerException ?? fault}",
                            member,
                            file,
                            line
                        );
                });
    }
}
