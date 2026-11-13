using System.CommandLine;
using Magic.Debugging;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>name</c>: shows a name in the title bar of the window until the run ends.
/// </summary>
/// <remarks>
/// With it, a person can tell whose window it is when several run at once.
/// </remarks>
internal static class NameCommand
{
    public static Command Create(App app)
    {
        Argument<string> text = new("text") { Description = "The name, in quotes when it has spaces" };
        Command name = new("name", "Names the window, shown in its title bar until the run ends") { text };
        name.SetAction(parsed => NameWindow(parsed.GetRequiredValue(text)));

        return name;

        void NameWindow(string title)
        {
            if (app.Window is not { } window)
            {
                Log.Info($"There is no window to name {title}.");

                return;
            }

            window.Title = title;
        }
    }
}
