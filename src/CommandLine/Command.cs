using System.CommandLine;
using System.CommandLine.Invocation;

namespace Magic.CommandLine;

/// <summary>
/// An option's effect as a lambda. Non-terminating, so the command's own action still runs after it.
/// </summary>
internal sealed class Command(Action<ParseResult> run) : SynchronousCommandLineAction
{
    public override bool Terminating => false;

    public override int Invoke(ParseResult parseResult)
    {
        run(parseResult);
        return 0;
    }
}
