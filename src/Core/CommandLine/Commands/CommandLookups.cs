using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Utils;

namespace Magic.CommandLine.Commands;

/// <summary>
/// What several commands check or look up the same way: an open project, an asset by its path or name, the player,
/// and a position typed as x y z.
/// </summary>
internal static class CommandLookups
{
    /// <summary>
    /// Whether a project is open for a command that needs one to <paramref name="what"/>; it logs an error when not.
    /// </summary>
    public static bool IsProjectOpen(App app, string what)
    {
        if (app.Project is not null)
            return true;

        Log.Error($"No project is running to {what}.");

        return false;
    }

    /// <summary>
    /// Writes <paramref name="json"/> to <paramref name="file"/>, or to standard output when no file is named.
    /// </summary>
    /// <remarks>
    /// A file gets the JSON indented, for a person to read, and <paramref name="what"/> says in the log what is in it.
    /// </remarks>
    public static void Print(IFileSystem files, string? file, JsonObject json, string what)
    {
        string text = json.ToJsonString(Json.Indented);
        if (file is null)
        {
            Terminal.Write(text);

            return;
        }

        Result written = files.Write(file, text);
        if (written.Ok)
            Log.Info($"{what} {file}.");
        else
            Log.Error($"{file} was not written: {written.Error}");
    }

    /// <summary>
    /// The asset with the id <paramref name="askedId"/>, or why there is none.
    /// </summary>
    public static Result<IndexedFile> FindAssetById(AssetManager assets, string askedId)
    {
        if (!ulong.TryParse(askedId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number))
            return Result.Failure($"'{askedId}' is not an id.");

        return assets.Database.Index.ById.TryGetValue(number, out IndexedFile? indexed)
            ? Result.Success(indexed)
            : Result.Failure($"no asset has the id {number}.");
    }

    /// <summary>
    /// The asset at the path <paramref name="asked"/>, or else the first whose file has that name, as spawn and
    /// open take one; or why there is none.
    /// </summary>
    public static Result<IndexedFile> FindAssetByPath(AssetManager assets, string asked)
    {
        if (assets.Database.Index.ByPath.TryGetValue(asked, out IndexedFile? indexed))
            return Result.Success(indexed);

        IndexedFile? named = assets
            .Database.Index.ByPath.Values.OrderBy(other => other.Path, StringComparer.Ordinal)
            .FirstOrDefault(other =>
                string.Equals(Path.GetFileNameWithoutExtension(other.Path), asked, StringComparison.OrdinalIgnoreCase)
            );

        return named is not null
            ? Result.Success(named)
            : Result.Failure($"no asset is at {asked}, and no asset's file is named that.");
    }

    /// <summary>
    /// The <typeparamref name="T"/> at <paramref name="pathOrName"/>, or else the first whose file has that name.
    /// </summary>
    /// <remarks>
    /// The name is in any case and without its extension (<c>Tree</c> for <c>Prefabs/Tree.prefab</c>). It is none
    /// when there is neither.
    /// </remarks>
    public static Handle<T> FindAsset<T>(AssetManager assets, string pathOrName)
        where T : IAsset<T>
    {
        Handle<T> atPath = assets.Find<T>(pathOrName);
        if (atPath != Handle<T>.None)
            return atPath;

        foreach (string path in assets.Database.Index.ByPath.Keys)
        {
            if (!string.Equals(Path.GetFileNameWithoutExtension(path), pathOrName, StringComparison.OrdinalIgnoreCase))
                continue;

            Handle<T> named = assets.Find<T>(path);
            if (named != Handle<T>.None)
                return named;
        }

        return Handle<T>.None;
    }

    /// <summary>
    /// The transform of the first entity tagged player that has one; null when none has.
    /// </summary>
    public static Transform? FindPlayer(IEcs ecs)
    {
        foreach (EcsChunk<Tags, Transform> chunk in ecs.Query<Tags, Transform>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                if (chunk.First[i].Has(Tag.Player))
                    return chunk.Second[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The three numbers of <paramref name="xyz"/> as a position, or why they are not one.
    /// </summary>
    public static Result<Vector3> ParsePosition(ReadOnlySpan<string> xyz)
    {
        if (xyz.Length != 3)
            return Result.Failure($"a position is three numbers, x y z, not {xyz.Length}.");

        Span<float> numbers = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!float.TryParse(xyz[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                return Result.Failure($"'{xyz[i]}' is not a number.");
        }

        return Result.Success(new Vector3(numbers));
    }
}
