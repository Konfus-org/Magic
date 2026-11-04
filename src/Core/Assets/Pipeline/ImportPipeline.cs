using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Pipes;
using Magic.Serialization;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// Makes the asset from its file, unless the cache had it: by the gem that reads its type, else by Core itself for a type
/// marked <see cref="AssetFormatAttribute"/>.
/// </summary>
internal sealed class ImportPipeline<T> : IPipeline<AssetLoad<T>>
    where T : IAsset<T>
{
    public async ValueTask<AssetLoad<T>> RunAsync(AssetLoad<T> state, CancellationToken cancel)
    {
        if (state.Asset is not null)
            return state;

        Result<T> read = state.Loader is { } loader
            ? await loader
                .LoadAsync(
                    new AssetFile(state.Id, state.Path, state.Bytes, state.Metadata, state.Version),
                    state.Progress,
                    cancel
                )
                .ConfigureAwait(false)
            : ReadBuiltIn(state.Bytes, state.Settings);

        return read.Ok ? state with { Asset = read } : state.ToNotAsset(read.Error);
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
}
