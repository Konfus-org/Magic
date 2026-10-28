using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Magic.Debugging;

public static class Assert
{
    /// <summary>
    /// State that cannot be: when <paramref name="condition"/> is false, the run <see cref="Crash.Now"/>s, with the
    /// condition's source as the message, and <paramref name="message"/> after it. On in every build.
    /// </summary>
    public static void True(
        [DoesNotReturnIf(false)] bool condition,
        string message = "",
        [CallerArgumentExpression(nameof(condition))] string expression = "",
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        if (!condition)
            Crash.Now(FormatFailed(expression, message), member, file, line);
    }

    /// <summary>
    /// State that cannot be: when <paramref name="condition"/> is true, the run <see cref="Crash.Now"/>s, with the
    /// condition's source as the message, and <paramref name="message"/> after it. On in every build.
    /// </summary>
    public static void False(
        [DoesNotReturnIf(true)] bool condition,
        string message = "",
        [CallerArgumentExpression(nameof(condition))] string expression = "",
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        if (condition)
            Crash.Now(FormatFailed(expression, message), member, file, line);
    }

    [DoesNotReturn]
    public static void Now(
        string message = "",
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    ) => Crash.Now(message, member, file, line);

    private static string FormatFailed(string expression, string message) =>
        message.Length == 0 ? $"Assert failed: {expression}" : $"Assert failed: {expression}: {message}";
}
