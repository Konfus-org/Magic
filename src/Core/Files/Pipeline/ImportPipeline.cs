using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Caching;
using Magic.Serialization;
using Magic.Utils;

namespace Magic.Files.Pipeline;

/// <summary>
/// Loads one <typeparamref name="T"/> on a worker, for several assets at once, a step at a time over its
/// <see cref="AssetLoad{T}"/>: reads the file and its <c>.meta</c>, looks in the cache, makes the asset, and keeps what it
/// made in the cache. It is made by the gem that reads its type, else by Core itself for a type marked
/// <see cref="AssetFormatAttribute"/>.
/// </summary>
internal static class ImportPipeline<T>
    where T : IAsset<T>
{
    public static async Task<AssetLoad<T>> RunAsync(
        AssetLoad<T> load,
        IFileSystem files,
        Caches caches,
        CancellationToken cancel
    )
    {
        load = await ReadFileAsync(load, files, cancel).ConfigureAwait(false);
        if (load.IsFailed)
            return load;

        load = ReadMetadata(load);
        if (load.IsFailed)
            return load;

        load = await ReadCacheAsync(load, caches, cancel).ConfigureAwait(false);
        if (load.Asset is not null)
            return load;

        load = await ImportAsync(load, cancel).ConfigureAwait(false);
        await WriteCacheAsync(load, caches, cancel).ConfigureAwait(false);

        return load;
    }

    /// <summary>
    /// <paramref name="settings"/>, a <c>.meta</c>'s, read as the values of <typeparamref name="T"/>'s
    /// <see cref="AssetMetaAttribute"/> properties, each in the type of its property, one it leaves out its
    /// initializer; or why they are not: a key no marked property has, or a value not of its property's type.
    /// </summary>
    internal static Result<AssetMetadata> ReadSettings(JsonElement settings)
    {
        // A type made with its initializers is where the defaults are; one with no parameterless constructor has none.
        object? defaults = typeof(T).GetConstructor(Type.EmptyTypes) is null ? null : Activator.CreateInstance<T>();
        Dictionary<string, PropertyInfo> byKey = new(StringComparer.Ordinal);
        ImmutableDictionary<string, object?>.Builder values = ImmutableDictionary.CreateBuilder<string, object?>(
            StringComparer.Ordinal
        );
        foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<AssetMetaAttribute>() is null)
                continue;

            byKey.Add(JsonNamingPolicy.CamelCase.ConvertName(property.Name), property);
            values[property.Name] =
                defaults is not null ? property.GetValue(defaults)
                : property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType)
                : null;
        }

        foreach (JsonProperty setting in settings.EnumerateObject())
        {
            if (!byKey.TryGetValue(setting.Name, out PropertyInfo? property))
            {
                string takes = byKey.Count == 0 ? "none" : string.Join(", ", byKey.Keys);
                return Result.Failure(
                    $"its .meta sets \"{setting.Name}\", which a {typeof(T).Name} does not take; it takes {takes}."
                );
            }

            Result<object> value = Json.Read(setting.Value, property.PropertyType);
            if (!value.Ok)
                return Result.Failure(
                    $"its .meta's \"{setting.Name}\" is not a {property.PropertyType.Name}: {value.Error}"
                );

            values[property.Name] = value.Value;
        }

        return Result.Success(new AssetMetadata(values.ToImmutable()));
    }

    /// <summary>
    /// <paramref name="bytes"/> read by Core itself, in the <see cref="AssetFileFormat"/> its type is marked with, with
    /// what its <c>.meta</c> sets, <paramref name="settings"/>, already read against the type's
    /// <see cref="AssetMetaAttribute"/> properties, set over it; else no gem the project lists reads the type.
    /// </summary>
    internal static Result<T> ReadBuiltIn(ReadOnlyMemory<byte> bytes, JsonElement settings)
    {
        if (typeof(T).GetCustomAttribute<AssetFormatAttribute>() is not { } format)
            return Result.Failure($"no gem the project lists reads {typeof(T).Name}s.");

        if (format.Format == AssetFileFormat.Json && settings.GetPropertyCount() == 0)
            return Json.Read<T>(bytes.Span);

        JsonObject json;
        if (format.Format == AssetFileFormat.Json)
        {
            Result<JsonObject> read = Json.Read<JsonObject>(bytes.Span);
            if (!read.Ok)
                return Result.Failure(read.Error);

            json = read.Value;
        }
        else
        {
            ReadOnlySpan<byte> text = bytes.Span;
            if (text.StartsWith(Encoding.UTF8.Preamble))
                text = text[Encoding.UTF8.Preamble.Length..];

            json = new JsonObject { ["text"] = Encoding.UTF8.GetString(text) };
        }

        foreach (JsonProperty setting in settings.EnumerateObject())
        {
            json[setting.Name] = JsonNode.Parse(setting.Value.GetRawText());
        }

        Result<JsonElement> element = Json.Write(json, typeof(JsonObject));

        return element.Ok ? Json.Read<T>(element.Value) : Result.Failure(element.Error);
    }

    /// <summary>
    /// Reads the file and works out its version. A file the type's loader takes no such extension of is refused before it
    /// is read.
    /// </summary>
    private static async Task<AssetLoad<T>> ReadFileAsync(
        AssetLoad<T> load,
        IFileSystem files,
        CancellationToken cancel
    )
    {
        if (load.Loader is { } loader && !HasExtension(load.Path, loader.Extensions))
            return load.ToNotAsset($"a {typeof(T).Name}'s file ends in {string.Join(", ", loader.Extensions)}.");

        Result<byte[]> bytes = await files.ReadBytesAsync(load.File, cancel).ConfigureAwait(false);
        if (!bytes.Ok)
            return load.ToFailed($"{load.Path} could not be read: {bytes.Error}");

        return load with
        {
            Bytes = bytes.Value,
            Version = AssetIndex.ComputeVersion(bytes.Value, load.Settings),
        };
    }

    /// <summary>
    /// Whether <paramref name="path"/> ends in one of <paramref name="extensions"/>.
    /// </summary>
    private static bool HasExtension(string path, ImmutableArray<string> extensions)
    {
        foreach (string extension in extensions)
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static AssetLoad<T> ReadMetadata(AssetLoad<T> load)
    {
        Result<AssetMetadata> metadata = ReadSettings(load.Settings);

        return metadata.Ok ? load with { Metadata = metadata.Value } : load.ToNotAsset(metadata.Error);
    }

    /// <summary>
    /// Reads what was made of the file before, by its id and version, when its loader is also an <see cref="ICache{T}"/>.
    /// </summary>
    private static async Task<AssetLoad<T>> ReadCacheAsync(AssetLoad<T> load, Caches caches, CancellationToken cancel)
    {
        if (load.Loader is not ICache<T> cache)
            return load;

        Result<T> cached = await caches.ReadAsync(load.Id, load.Version, cache, cancel).ConfigureAwait(false);

        return cached.Ok ? load with { Asset = cached } : load;
    }

    private static async Task<AssetLoad<T>> ImportAsync(AssetLoad<T> load, CancellationToken cancel)
    {
        Result<T> read = load.Loader is { } loader
            ? await loader
                .LoadAsync(
                    new AssetFile(load.Id, load.Path, load.Bytes, load.Metadata, load.Version),
                    load.Progress,
                    cancel
                )
                .ConfigureAwait(false)
            : ReadBuiltIn(load.Bytes, load.Settings);

        return read.Ok ? load with { Asset = read } : load.ToNotAsset(read.Error);
    }

    /// <summary>
    /// Keeps what a loader that is also an <see cref="ICache{T}"/> made, by the file's id and version.
    /// </summary>
    private static async Task WriteCacheAsync(AssetLoad<T> load, Caches caches, CancellationToken cancel)
    {
        if (load.Loader is ICache<T> cache && load.Asset is { Ok: true, Value: { } asset })
            await caches.WriteAsync(load.Id, load.Version, asset, cache, cancel).ConfigureAwait(false);
    }
}
