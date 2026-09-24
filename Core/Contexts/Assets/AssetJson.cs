using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magic.Contexts.Assets;

/// <summary>
/// How every asset file and sidecar is read and written: camelCase keys, snake_case enum names
/// (<c>clamp_to_edge</c>), fields included so System.Numerics vectors serialise as <c>{x, y, z, w}</c>,
/// comments and trailing commas tolerated. No custom converters: a <see cref="Handle{T}"/> is <c>{ "id": N }</c>.
/// </summary>
public static class AssetJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
}
