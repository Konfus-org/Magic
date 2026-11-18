using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using Magic.Assets;
using Magic.Debugging;
using Magic.Files;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>new</c>: makes a project or gem from the <c>dotnet new</c> templates of Magic, and passes on each option that
/// Magic does not know, such as <c>-o</c>; <c>new asset</c> makes the file of an
/// asset.
/// </summary>
internal static class NewCommand
{
    public static Command Create(App app, IFileSystem files, AssetManager assets, CommandQueue queue)
    {
        Argument<string> template = new("template") { Description = "Which template: X in dotnet new magic-X" };
        Argument<string> name = new("name") { Description = "What to call it" };
        Command make = new("new", "Makes a project or gem from Magic's dotnet new templates, or the file of an asset")
        {
            template,
            name,
            AssetCommand.Create(app, files, assets, queue),
        };

        // Options Magic doesn't know, like -o or --force, go to dotnet new.
        make.TreatUnmatchedTokensAsErrors = false;
        make.SetAction(parsed =>
            CreateFromTemplate(parsed.GetRequiredValue(template), parsed.GetRequiredValue(name), parsed.UnmatchedTokens)
        );

        return make;

        static void CreateFromTemplate(string template, string name, IReadOnlyList<string> options)
        {
            string fullTemplate = $"magic-{template}";
            try
            {
                using Process process = Process.Start("dotnet", ["new", fullTemplate, "--name", name, .. options]);

                // Holds the terminal until dotnet new ends: at launch nothing else runs yet, and typed while a project
                // runs it pauses only the terminal's pump.
                process.WaitForExit();
                if (process.ExitCode != 0)
                    Log.Error($"Couldn't make {name} from {fullTemplate}: dotnet exited with code {process.ExitCode}.");
            }
            catch (Win32Exception ex)
            {
                Log.Error($"Couldn't make {name} from {fullTemplate}: dotnet didn't start: {ex.Message}");
            }
        }
    }
}
