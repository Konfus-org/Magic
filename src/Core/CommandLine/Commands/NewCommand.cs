using System.CommandLine;
using Magic.Debugging;
using Magic.Utils;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>new</c>: makes a project or gem from Magic's <c>dotnet new</c> templates, passing on any option Magic doesn't know,
/// such as <c>-o</c>.
/// </summary>
internal sealed class NewCommand : Command
{
    public NewCommand()
        : base("new", "Makes a project or gem from Magic's dotnet new templates")
    {
        Argument<string> template = new("template") { Description = "Which template: X in dotnet new magic-X" };
        Argument<string> name = new("name") { Description = "What to call it" };
        Arguments.Add(template);
        Arguments.Add(name);

        // Options Magic doesn't know, like -o or --force, go to dotnet new.
        TreatUnmatchedTokensAsErrors = false;
        Action = new CliCommand(parsed =>
            Create(parsed.GetRequiredValue(template), parsed.GetRequiredValue(name), parsed.UnmatchedTokens)
        );
    }

    private static void Create(string template, string name, IReadOnlyList<string> options)
    {
        string fullTemplate = $"magic-{template}";
        Result created = Processes.Run("dotnet", ["new", fullTemplate, "--name", name, .. options]);
        if (!created.Ok)
            Log.Error($"Couldn't make {name} from {fullTemplate}: {created.Error}");
    }
}
