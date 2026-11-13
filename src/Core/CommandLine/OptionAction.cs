using System.CommandLine;
using System.CommandLine.Invocation;

namespace Magic.CommandLine;

/// <summary>
/// The effect of an option, as a lambda.
/// </summary>
/// <remarks>
/// It does not end the command, so the action of the command still runs after it.
/// </remarks>
internal sealed class OptionAction(Action<ParseResult> run) : SynchronousCommandLineAction
{
    public override bool Terminating => false;

    public override int Invoke(ParseResult parseResult)
    {
        run(parseResult);
        return 0;
    }
}
