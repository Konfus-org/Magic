using System.Collections.Frozen;
using System.Collections.Immutable;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Magic.Debugging;
using Magic.Files;
using Magic.Serialization;
using Magic.Utils;

namespace Magic.Assets;

/// <summary>
/// What is under the asset directories at one moment: every file with a <c>.meta</c> sidecar, by its id, its path and
/// its folder, and the built-in ones by their name besides. A value: indexing again makes a new one, and what differs
/// between two is <see cref="Diff"/>.
/// </summary>
internal sealed record AssetIndex
{
    private const string Sidecar = ".meta";

    public static AssetIndex Empty { get; } =
        new()
        {
            ById = FrozenDictionary<ulong, IndexedFile>.Empty,
            ByPath = FrozenDictionary<string, IndexedFile>.Empty,
            ByFolder = FrozenDictionary<string, ImmutableArray<string>>.Empty,
            BuiltIn = FrozenDictionary<string, IndexedFile>.Empty,
        };

    public required FrozenDictionary<ulong, IndexedFile> ById { get; init; }

    /// <summary>
    /// Each path's file, from the first asset directory that has one there.
    /// </summary>
    public required FrozenDictionary<string, IndexedFile> ByPath { get; init; }

    /// <summary>
    /// The paths directly in each folder, an asset directory's own being the empty folder.
    /// </summary>
    public required FrozenDictionary<string, ImmutableArray<string>> ByFolder { get; init; }

    /// <summary>
    /// The built-in files, Magic's own, by their path without the extension (<c>Materials/Default</c>): where the
    /// defaults and failures are, and only there.
    /// </summary>
    public required FrozenDictionary<string, IndexedFile> BuiltIn { get; init; }

    /// <summary>
    /// What <paramref name="directories"/> hold, read one by one, the first winning a path two of them have.
    /// <paramref name="builtIn"/> is the one whose files are built in. A file with an id another has already is an
    /// error and gives no asset, unless it is at that file's path in a later directory, which the first overrides.
    /// </summary>
    public static AssetIndex Create(IFileSystem files, ImmutableArray<string> directories, string builtIn)
    {
        Dictionary<ulong, IndexedFile> byId = [];
        Dictionary<string, IndexedFile> byPath = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> byFolder = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IndexedFile> builtInFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directories)
        {
            bool isBuiltIn = string.Equals(directory, builtIn, StringComparison.OrdinalIgnoreCase);
            foreach (IndexedFile indexed in ReadDirectory(files, directory))
            {
                string path = indexed.Path;

                // An id an earlier directory has already: at the same path, that directory's file overrides this one,
                // and is found as the built-in one when this one is; at another path, it is an error.
                if (byId.TryGetValue(indexed.Id, out IndexedFile? other))
                {
                    if (!string.Equals(other.Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Error(
                            $"{path} has the id {indexed.Id}, which {other.Path} has already; it is not an asset until "
                                + "one of them has another."
                        );
                    }
                    else if (isBuiltIn)
                    {
                        builtInFiles.TryAdd(WithoutExtension(path), other);
                    }

                    continue;
                }

                byId.Add(indexed.Id, indexed);
                if (isBuiltIn)
                    builtInFiles.TryAdd(WithoutExtension(path), indexed);

                // The first directory to have a path wins it, and only that one's file is listed in its folder.
                if (!byPath.TryAdd(path, indexed))
                    continue;

                int slash = path.LastIndexOf('/');
                string folder = slash < 0 ? string.Empty : path[..slash];
                if (!byFolder.TryGetValue(folder, out List<string>? paths))
                {
                    paths = [];
                    byFolder.Add(folder, paths);
                }

                paths.Add(path);
            }
        }

        Dictionary<string, ImmutableArray<string>> folders = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string folder, List<string> paths) in byFolder)
        {
            folders.Add(folder, [.. paths]);
        }

        return new()
        {
            ById = byId.ToFrozenDictionary(),
            ByPath = byPath.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            ByFolder = folders.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            BuiltIn = builtInFiles.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// The assets to make again once the index went from <paramref name="before"/> to <paramref name="now"/> after the
    /// watches saw <paramref name="touched"/>, paths from their asset directories: those whose file is gone, is another
    /// file now, or was touched, its <c>.meta</c> counting as its file.
    /// </summary>
    public static HashSet<ulong> Diff(AssetIndex before, AssetIndex now, IReadOnlySet<string> touched)
    {
        HashSet<ulong> changed = [];
        foreach (IndexedFile old in before.ById.Values)
        {
            if (!now.ById.ContainsKey(old.Id))
                changed.Add(old.Id);
        }

        foreach (IndexedFile file in now.ById.Values)
        {
            before.ById.TryGetValue(file.Id, out IndexedFile? old);
            if (!string.Equals(old?.File, file.File, StringComparison.Ordinal) || touched.Contains(file.Path))
                changed.Add(file.Id);
        }

        return changed;
    }

    /// <summary>
    /// A hash of a file's <paramref name="bytes"/> and its sidecar's <paramref name="settings"/>, which changes when
    /// either does: the version of the asset they make.
    /// </summary>
    public static ulong ComputeVersion(ReadOnlySpan<byte> bytes, JsonElement settings)
    {
        XxHash3 hash = new();
        hash.Append(bytes);
        hash.Append(Encoding.UTF8.GetBytes(settings.GetRawText()));

        return hash.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// The built-in asset named <paramref name="name"/> in <paramref name="folder"/>, whatever its extension, or in its
    /// own folder of that name for an asset that is a folder; null when there is none.
    /// </summary>
    public IndexedFile? FindBuiltIn(string folder, string name) =>
        BuiltIn.TryGetValue($"{folder}/{name}", out IndexedFile? found)
        || BuiltIn.TryGetValue($"{folder}/{name}/{name}", out found)
            ? found
            : null;

    /// <summary>
    /// <paramref name="path"/> without its extensions: <c>Materials/Default</c> for <c>Materials/Default.mat</c>.
    /// </summary>
    private static string WithoutExtension(string path)
    {
        int name = path.LastIndexOf('/') + 1;
        int dot = path.IndexOf('.', name);

        return dot < 0 ? path : path[..dot];
    }

    /// <summary>
    /// Every file with a sidecar under <paramref name="directory"/>, by its path from it. A sidecar without its file is
    /// warned about, and a broken one is an error and gives no asset.
    /// </summary>
    private static List<IndexedFile> ReadDirectory(IFileSystem files, string directory)
    {
        List<IndexedFile> found = [];
        Result<string[]> sidecars = files.EnumerateFiles(directory, "*" + Sidecar, recursive: true);
        if (!sidecars.Ok)
        {
            Log.Error($"The assets under {directory} could not be listed: {sidecars.Error}");
            return found;
        }

        foreach (string sidecar in sidecars.Value)
        {
            string file = sidecar[..^Sidecar.Length];
            string path = file[(directory.Length + 1)..].Replace('\\', '/');
            if (!files.FileExists(file))
            {
                Log.Warn($"{path}{Sidecar} has no {path} beside it.");
                continue;
            }

            Result<IndexedFile> read = ReadSidecar(files, sidecar, path, file);
            if (!read.Ok)
            {
                Log.Error($"{path}{Sidecar} is not a sidecar, so {path} is not an asset: {read.Error}");
                continue;
            }

            found.Add(read.Value);
        }

        return found;
    }

    /// <summary>
    /// The asset the sidecar at <paramref name="sidecar"/> makes <paramref name="file"/>, at <paramref name="path"/>:
    /// its id, its type's name, and the rest of it, the import settings its loader reads.
    /// </summary>
    private static Result<IndexedFile> ReadSidecar(IFileSystem files, string sidecar, string path, string file)
    {
        Result<byte[]> bytes = files.ReadBytes(sidecar);
        if (!bytes.Ok)
            return Result.Failure(bytes.Error);

        Result<Dictionary<string, JsonElement>> read = Json.Read<Dictionary<string, JsonElement>>(bytes.Value);
        if (!read.Ok)
            return Result.Failure(read.Error);

        if (
            !read.Value.Remove("id", out JsonElement id)
            || id.ValueKind != JsonValueKind.Number
            || !id.TryGetUInt64(out ulong value)
            || value == 0
        )
        {
            return Result.Failure("it needs an \"id\", a whole number above 0.");
        }

        if (
            !read.Value.Remove("type", out JsonElement type)
            || type.ValueKind != JsonValueKind.String
            || type.GetString() is not { Length: > 0 } typeName
        )
        {
            return Result.Failure("it needs a \"type\", the name of the asset type it is, such as \"Material\".");
        }

        Result<JsonElement> settings = Json.Write(read.Value, typeof(Dictionary<string, JsonElement>));

        return settings.Ok
            ? Result.Success(new IndexedFile(value, typeName, path, file, settings.Value))
            : Result.Failure(settings.Error);
    }
}

/// <summary>
/// A file with a sidecar under an asset directory.
/// </summary>
/// <param name="Id">The id its sidecar holds.</param>
/// <param name="Type">The name of the asset type its sidecar says it is, such as <c>Material</c>.</param>
/// <param name="Path">Where it is from its asset directory, with <c>/</c>.</param>
/// <param name="File">Where it is on disk.</param>
/// <param name="Settings">Its sidecar without the id and the type: its loader's import settings.</param>
internal sealed record IndexedFile(ulong Id, string Type, string Path, string File, JsonElement Settings);
