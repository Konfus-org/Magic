using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magic.Contexts.Assets;

/// <summary>
/// How every asset file and sidecar is read and written: camelCase keys, snake_case enum names
/// (<c>clamp_to_edge</c>), fields included so System.Numerics vectors serialise as <c>{x, y, z, w}</c>,
/// comments and trailing commas tolerated, infinities written by name. A <see cref="Handle{T}"/> is <c>{ "id": N }</c>;
/// the one custom converter is <see cref="PassListConverter"/>, named by the type it reads.
/// </summary>
public static class AssetJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, // Camera.Far = +Infinity round-trips as "Infinity"
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
}
