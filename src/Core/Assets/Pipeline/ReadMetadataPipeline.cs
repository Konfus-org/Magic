using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Magic.Pipelines;
using Magic.Serialization;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// Reads the file's <c>.meta</c> settings against <typeparamref name="T"/>'s <see cref="AssetMetaAttribute"/> properties.
/// </summary>
internal sealed class ReadMetadataPipeline<T> : IPipeline<AssetLoad<T>>
    where T : IAsset<T>
{
    public ValueTask<AssetLoad<T>> RunAsync(AssetLoad<T> state, CancellationToken cancel)
    {
        if (state.IsFailed)
            return ValueTask.FromResult(state);

        Result<AssetMetadata> metadata = Read(state.Settings);

        return ValueTask.FromResult(
            metadata.Ok ? state with { Metadata = metadata.Value } : state.ToNotAsset(metadata.Error)
        );
    }

    /// <summary>
    /// <paramref name="settings"/>, a <c>.meta</c>'s, read as the values of <typeparamref name="T"/>'s
    /// <see cref="AssetMetaAttribute"/> properties, each in the type of its property, one it leaves out its
    /// initializer; or why they are not: a key no marked property has, or a value not of its property's type.
    /// </summary>
    internal static Result<AssetMetadata> Read(JsonElement settings)
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
}
