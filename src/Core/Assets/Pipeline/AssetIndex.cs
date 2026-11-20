using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Magic.Debugging;
using Magic.Files;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// Every file with a <c>.meta</c> sidecar under the asset directories at one moment, by id, path and folder, and the
/// built-in files by name.
/// </summary>
/// <remarks>
/// It is a value: indexing again makes a new one, and <see cref="Diff"/> gives what differs between two.
/// </remarks>
internal sealed record AssetIndex
{
    /// <summary>
    /// What a file's sidecar ends in: its name, then this.
    /// </summary>
    public const string Sidecar = ".meta";

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
    /// defaults are, and only there.
    /// </summary>
    public required FrozenDictionary<string, IndexedFile> BuiltIn { get; init; }

    /// <summary>
    /// What <paramref name="directories"/> hold, as <paramref name="scans"/> has them; when two hold the same path, the
    /// first wins.
    /// </summary>
    /// <remarks>
    /// <paramref name="builtIn"/> is the directory whose files are built in. A file with an id that another file has
    /// already is an error and gives no asset, unless it is at the same path in a later directory, which the first
    /// overrides.
    /// </remarks>
    public static AssetIndex Create(AssetScans scans, ImmutableArray<string> directories, string builtIn)
    {
        Dictionary<ulong, IndexedFile> byId = [];
        Dictionary<string, IndexedFile> byPath = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> byFolder = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IndexedFile> builtInFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directories)
        {
            bool isBuiltIn = string.Equals(directory, builtIn, StringComparison.OrdinalIgnoreCase);
            foreach (IndexedFile indexed in scans.Get(directory))
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

                string folder = GetFolder(path);
                if (!byFolder.TryGetValue(folder, out List<string>? paths))
                {
                    paths = [];
                    byFolder.Add(folder, paths);
                }

                paths.Add(path);
            }
        }

        return new()
        {
            ById = byId.ToFrozenDictionary(),
            ByPath = byPath.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            ByFolder = byFolder.ToFrozenDictionary(
                pair => pair.Key,
                pair => pair.Value.ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase
            ),
            BuiltIn = builtInFiles.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// The assets to make again after the index went from <paramref name="before"/> to <paramref name="now"/> and the
    /// watches saw <paramref name="touched"/>.
    /// </summary>
    /// <remarks>
    /// These are the assets whose file is gone, is another file now, or was touched. <paramref name="touched"/> holds
    /// paths from their asset directories, and a <c>.meta</c> counts as its file.
    /// </remarks>
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
    /// A hash of a file's <paramref name="bytes"/>, its sidecar's <paramref name="settings"/> and the
    /// <paramref name="importerVersion"/> of what reads it, which changes when any does: the version of the asset they
    /// make.
    /// </summary>
    public static ulong ComputeVersion(ReadOnlySpan<byte> bytes, JsonElement settings, int importerVersion)
    {
        XxHash3 hash = new();
        hash.Append(bytes);
        hash.Append(Encoding.UTF8.GetBytes(settings.GetRawText()));
        hash.Append(BitConverter.GetBytes(importerVersion));

        return hash.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// The folder <paramref name="path"/> is in: <c>Materials</c> for <c>Materials/Default.mat</c>, empty at the top.
    /// Each place that needs the folder of a path gets it here, so the folders match.
    /// </summary>
    public static string GetFolder(string path)
    {
        int slash = path.LastIndexOf('/');

        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>
    /// The id to give <paramref name="file"/>, which has no <c>.meta</c> yet: one above the highest id of the asset
    /// directory that holds it, so that the ids of a project, of a gem and of Magic stay apart, and past each id that
    /// another asset directory of the run already holds.
    /// </summary>
    /// <remarks>
    /// It fails for a file that no asset directory of the run holds. This is the one rule that gives a new id.
    /// </remarks>
    public Result<ulong> GetNextId(string file)
    {
        if (!FindRoot(file, out _, out ulong highest))
            return Result.Failure($"{file} is in no folder that this run reads assets from.");

        ulong id = highest + 1;
        while (ById.ContainsKey(id))
            id++;

        return Result.Success(id);
    }

    /// <summary>
    /// The path that <paramref name="file"/> has as an asset: its path under the asset directory that holds it, as
    /// <c>Materials/Carpet.mat</c>.
    /// </summary>
    /// <remarks>
    /// It fails for a file that no asset directory of the run holds.
    /// </remarks>
    public Result<string> GetAssetPath(string file) =>
        FindRoot(file, out string? root, out _)
            ? Result.Success(file.Replace('\\', '/')[(root.Length + 1)..])
            : Result.Failure($"{file} is in no folder that this run reads assets from.");

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
    /// The asset the sidecar at <paramref name="sidecar"/> makes <paramref name="file"/>, at <paramref name="path"/>:
    /// its id, its type's name, and the rest of it, the import settings its importer reads.
    /// </summary>
    internal static Result<IndexedFile> ReadSidecar(IFileSystem files, string sidecar, string path, string file)
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
    /// The asset directory that holds <paramref name="file"/>, and the highest id that an asset of the directories
    /// holding it has; false when no asset directory of the run holds it.
    /// </summary>
    /// <remarks>
    /// The longest directory that holds the file is the one it belongs to, as the directory of a gem can be under the
    /// directory of a project.
    /// </remarks>
    private bool FindRoot(string file, [NotNullWhen(true)] out string? root, out ulong highest)
    {
        root = null;
        highest = 0;
        string asked = file.Replace('\\', '/');
        foreach (IndexedFile indexed in ById.Values)
        {
            string directory = indexed.File[..^(indexed.Path.Length + 1)].Replace('\\', '/');
            // The separator is part of the comparison, so that an Assets2 folder does not count as an Assets one.
            if (!asked.StartsWith($"{directory}/", StringComparison.OrdinalIgnoreCase))
                continue;

            if (root is null || directory.Length > root.Length)
                root = directory;

            highest = Math.Max(highest, indexed.Id);
        }

        return root is not null;
    }
}

/// <summary>
/// A file with a sidecar under an asset directory.
/// </summary>
/// <param name="Id">The id its sidecar holds.</param>
/// <param name="Type">The name of the asset type its sidecar says it is, such as <c>Material</c>.</param>
/// <param name="Path">Where it is from its asset directory, with <c>/</c>.</param>
/// <param name="File">Where it is on disk.</param>
/// <param name="Settings">Its sidecar without the id and the type: its importer's import settings.</param>
internal sealed record IndexedFile(ulong Id, string Type, string Path, string File, JsonElement Settings);
