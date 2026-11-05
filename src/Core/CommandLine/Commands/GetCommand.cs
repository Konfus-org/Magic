using System.Collections.Immutable;
using System.CommandLine;
using Magic.Debugging;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>get</c>: logs an entity's value, named by a path as <see cref="EntityPaths"/> reads it, on each entity the path
/// names.
/// </summary>
internal sealed class GetCommand : Command
{
    private readonly App _app;

    private readonly World _world;

    public GetCommand(App app, World world)
        : base("get", "Logs an entity's value, named as entity.component.member and on to any depth")
    {
        _app = app;
        _world = world;
        Argument<string> path = new("path")
        {
            Description = "The entity a tag or an id, then the component and its members",
        };
        Arguments.Add(path);
        Action = new CliCommand(parsed => LogEntityValue(parsed.GetRequiredValue(path)));
    }

    /// <summary>
    /// Logs the value at <paramref name="path"/> on each entity it names, or why there is none.
    /// </summary>
    private void LogEntityValue(string path)
    {
        if (!CommandLookups.IsProjectOpen(_app, $"get {path} in"))
            return;

        Result<ImmutableArray<string>> read = EntityPaths.Read(_world, path);
        if (!read.Ok)
        {
            Log.Error($"{path} was not read: {read.Error}");

            return;
        }

        foreach (string line in read.Value)
        {
            Log.Info($"{path} of {line}");
        }
    }
}
