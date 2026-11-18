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
/// What several commands check or look up the same way: an open project, an asset by its path, its id or its file's
/// name, the player, and a position typed as x y z.
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
    /// Prints <paramref name="asset"/> to standard output, as each command that makes or imports one does.
    /// </summary>
    public static void PrintAsset(IndexedFile asset) => Terminal.Write(DescribeAsset(asset).ToJsonString(Json.Options));

    /// <summary>
    /// The id, the path and the type of <paramref name="asset"/>, as each command that names an asset prints them.
    /// </summary>
    public static JsonObject DescribeAsset(IndexedFile asset) =>
        new()
        {
            ["id"] = asset.Id,
            ["path"] = asset.Path,
            ["type"] = asset.Type,
        };

    /// <summary>
    /// The asset at the path <paramref name="asked"/>, or the one with that id, or else the first whose file has that
    /// name, as every command that names an asset takes one; or why there is none.
    /// </summary>
    public static Result<IndexedFile> FindAsset(AssetManager assets, string asked)
    {
        if (FindIndexed(assets, asked) is { } indexed)
            return Result.Success(indexed);

        if (ulong.TryParse(asked, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
            return Result.Failure($"no asset has the id {id}.");

        return FindNamed(assets, asked).FirstOrDefault() is { } named
            ? Result.Success(named)
            : Result.Failure($"no asset is at {asked}, and no asset's file is named that.");
    }

    /// <summary>
    /// The asset that the first parts of the console path <paramref name="path"/> name, and the members of it that the
    /// parts after it name; null when no part of it names an asset.
    /// </summary>
    /// <remarks>
    /// The asset is its path under its project, gem or Magic, or its id, as <c>query asset</c> names one. The longest
    /// first parts that name an asset win, so that the dots of a path such as <c>Materials/Carpet.mat</c> stay with
    /// it. At least one member follows the asset: <c>set</c> writes a value of an asset, never the whole asset.
    /// </remarks>
    public static (IndexedFile Asset, string[] Members)? FindAssetPath(AssetManager assets, string path)
    {
        string[] parts = path.Split('.');
        for (int taken = parts.Length - 1; taken > 0; taken--)
        {
            if (FindIndexed(assets, string.Join('.', parts[..taken])) is { } asset)
                return (asset, parts[taken..]);
        }

        return null;
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

        foreach (IndexedFile other in FindNamed(assets, pathOrName))
        {
            Handle<T> named = assets.Find<T>(other.Path);
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

    /// <summary>
    /// The asset at the path <paramref name="named"/>, or the one with that id; null when there is neither.
    /// </summary>
    private static IndexedFile? FindIndexed(AssetManager assets, string named)
    {
        if (assets.Database.Index.ByPath.TryGetValue(named, out IndexedFile? indexed))
            return indexed;

        return
            ulong.TryParse(named, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)
            && assets.Database.Index.ById.TryGetValue(id, out IndexedFile? numbered)
            ? numbered
            : null;
    }

    /// <summary>
    /// The assets whose file is named <paramref name="name"/>, without its extension and in any case, in the order of
    /// their paths.
    /// </summary>
    private static IEnumerable<IndexedFile> FindNamed(AssetManager assets, string name) =>
        assets
            .Database.Index.ByPath.Values.Where(other =>
                string.Equals(Path.GetFileNameWithoutExtension(other.Path), name, StringComparison.OrdinalIgnoreCase)
            )
            .OrderBy(other => other.Path, StringComparer.Ordinal);
}
