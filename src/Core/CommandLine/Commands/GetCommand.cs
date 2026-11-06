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
internal static class GetCommand
{
    public static Command Create(App app, World world)
    {
        Argument<string> path = new("path")
        {
            Description = "The entity a tag or an id, then the component and its members",
        };
        Command get = new("get", "Logs an entity's value, named as entity.component.member and on to any depth")
        {
            path,
        };
        get.SetAction(parsed => LogEntityValue(parsed.GetRequiredValue(path)));

        return get;

        // Logs the value at the path on each entity it names, or why there is none.
        void LogEntityValue(string entityPath)
        {
            if (!CommandLookups.IsProjectOpen(app, $"get {entityPath} in"))
                return;

            Result<ImmutableArray<string>> read = EntityPaths.Read(world, entityPath);
            if (!read.Ok)
            {
                Log.Error($"{entityPath} was not read: {read.Error}");

                return;
            }

            foreach (string line in read.Value)
            {
                Log.Info($"{entityPath} of {line}");
            }
        }
    }
}
