using System.CommandLine;
using System.Linq;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>set</c>: sets a setting of a gem, by its key, or a value of an entity, of a whole component of it or of an
/// asset, named by a path, to a value.
/// </summary>
/// <remarks>
/// <see cref="ValueText"/> reads the value (<c>1 2 3</c> for a position), <see cref="EntityPaths"/> reads the path of
/// an entity, and <see cref="CommandLookups.FindAssetPath"/> the path of an asset. <c>export</c> writes a changed
/// asset back to its own file.
/// </remarks>
internal static class SetCommand
{
    public static Command Create(
        App app,
        Settings settings,
        AssetManager assets,
        World world,
        ConsoleCommands commands,
        CommandQueue queue
    )
    {
        // The assets that set changed, held so that the GC frees none of them before an export writes it: an asset
        // stays only while something holds it.
        Dictionary<ulong, object> changed = [];

        Argument<string> setting = new("setting")
        {
            Description =
                "A setting's key, as Rendering.Resolution; or a property of a part of a gem, as Window.Title; or the path to an entity's value or to a whole component: the entity a tag or an id, each part its name or initials; or the path to a value of an asset: the asset its path or its id, then its members",
        };
        Argument<string[]> value = new("value")
        {
            Description =
                "The value in JSON, in single quotes so that it keeps its double quotes, or one per field, as 1 2 3 for a position",
            Arity = ArgumentArity.ZeroOrMore,
        };
        Command set = new(
            "set",
            "Sets a setting, as Rendering.Resolution 2560 1440; or a property of a part of a gem, as Window.Title; or sets an entity's value, as entity.component.member and on to any depth, then the value; or a whole component, as entity.component then its JSON in single quotes; or a value of an asset, as Materials/Carpet.mat.params.color then its JSON in single quotes"
        )
        {
            setting,
            value,
        };
        set.SetAction(parsed => Set(parsed.GetRequiredValue(setting), parsed.GetValue(value) ?? []));

        return set;

        void Set(string what, string[] values)
        {
            // A setting for its key: the words read as the type the setting is declared as.
            if (settings.Options.FirstOrDefault(row => row.Key == what) is { Key: not null } option)
            {
                Result<object> parsed = ValueText.Parse(option.Type, values);
                Result set = parsed.Ok ? settings.Set(option.Key, parsed.Value) : Result.Failure(parsed.Error);
                if (set.Ok)
                    Log.Info($"Set {option.Key} to {string.Join(' ', values)}.");
                else
                    Log.Error($"{option.Key} was not set: {set.Error}");

                return;
            }

            // Else a property of the part of a gem that has the path.
            if (commands.FindService(what) is { } found)
            {
                Result set = ServicePaths.Write(found.Service, found.Type, what, values);
                if (set.Ok)
                    Log.Info($"Set {what} to {string.Join(' ', values)}.");
                else
                    Log.Error($"{what} was not set: {set.Error}");

                return;
            }

            // Else an entity's value, on each entity the path names that has the component.
            if (!CommandLookups.IsProjectOpen(app, $"set {what} in"))
                return;

            Result<int> written = EntityPaths.Write(world, what, values);
            if (written.Ok)
            {
                Log.Info(
                    $"Set {what} to {string.Join(' ', values)} on {written.Value} {(written.Value == 1 ? "entity" : "entities")}."
                );

                return;
            }

            // Else a value of the asset that the path names before its members.
            if (CommandLookups.FindAssetPath(assets, what) is { } asset)
            {
                SetAsset(asset, values);

                return;
            }

            Log.Error($"{what} was not set: {written.Error}");
        }

        // Loads the asset, which holds the lines after this one, then writes the values into it on the game thread,
        // where an asset is changed.
        void SetAsset((IndexedFile Asset, string[] Members) asset, string[] values)
        {
            Result<object> loaded = default;
            Task loading = LoadAsync();
            loading.FireAndForget();
            queue.Hold(loading, Write);

            async Task LoadAsync() =>
                loaded = await assets.LoadAsync(asset.Asset.Id, Terminal.Progress, app.Lifetime).ConfigureAwait(false);

            void Write()
            {
                string path = $"{asset.Asset.Path}.{string.Join('.', asset.Members)}";
                if (!loaded.Ok)
                {
                    Log.Error($"{path} was not set: {loaded.Error}");

                    return;
                }

                Result written = EntityPaths.WriteMembers(loaded.Value, loaded.Value.GetType(), asset.Members, values);
                if (!written.Ok)
                {
                    Log.Error($"{path} was not set: {written.Error}");

                    return;
                }

                changed[asset.Asset.Id] = loaded.Value;
                Log.Info($"Set {path} to {string.Join(' ', values)}.");
            }
        }
    }
}
