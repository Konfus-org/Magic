using System.Collections.Immutable;

namespace Magic.Assets.Pipeline;

/// <summary>
/// The values of an asset type's <see cref="AssetMetaAttribute"/> properties for one file: as its <c>.meta</c> sets
/// them, else their initializers.
/// </summary>
/// <remarks>
/// An <see cref="IAssetImporter{T}"/> reads its import settings from it:
/// <c>file.Metadata.Get&lt;TextureWrap&gt;(nameof(Texture.Wrap))</c>.
/// </remarks>
public sealed class AssetMetadata
{
    private readonly ImmutableDictionary<string, object?> _values;

    internal AssetMetadata(ImmutableDictionary<string, object?> values) => _values = values;

    /// <summary>
    /// The value of the marked property <paramref name="property"/>, by its name in C#.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// If the type does not mark <paramref name="property"/>, or its value is not a <typeparamref name="TValue"/>.
    /// </exception>
    public TValue Get<TValue>(string property)
    {
        if (!_values.TryGetValue(property, out object? value))
            throw new ArgumentException($"{property} is no property marked [AssetMeta].", nameof(property));

        return value is TValue typed
            ? typed
            : throw new ArgumentException(
                $"{property} is a {value?.GetType().Name ?? "null"}, not a {typeof(TValue).Name}.",
                nameof(property)
            );
    }
}
