using System.Globalization;
using System.IO;
using System.Numerics;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
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
