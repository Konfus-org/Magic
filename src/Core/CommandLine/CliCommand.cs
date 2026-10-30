using System.CommandLine;
using System.CommandLine.Invocation;

namespace Magic.CommandLine;

/// <summary>
/// An option's or a command's effect as a lambda. Non-terminating, so a command's own action still runs after its
/// options'.
/// </summary>
internal sealed class CliCommand(Action<ParseResult> run) : SynchronousCommandLineAction
{
    public override bool Terminating => false;

    public override int Invoke(ParseResult parseResult)
    {
        run(parseResult);
        return 0;
    }
}
