using Magic.Contexts.Frames;
using Magic.Contexts.Subscriptions;

namespace Magic.Interfaces;

/// <summary>
/// Commands typed at a console, such as the terminal Magic was started from. Main thread only.
/// </summary>
public interface ICommands
{
    /// <summary>
    /// Adds the command <paramref name="name"/> until the subscription is disposed. <paramref name="run"/> gets the
    /// words typed after the name, and runs on the main thread in <see cref="Phase.Update"/>.
    /// </summary>
    Subscription Register(string name, Action<ReadOnlySpan<string>> run);

    /// <summary>
    /// Runs <paramref name="line"/> as if it were typed at the terminal, now: a console of another kind, such as a
    /// window, hands its lines here.
    /// </summary>
    void Run(string line);
}
