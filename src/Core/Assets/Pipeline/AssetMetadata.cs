using System.Collections.Immutable;

namespace Magic.Assets.Pipeline;

/// <summary>
/// The values of an asset type's <see cref="AssetMetaAttribute"/> properties for one file: each as its <c>.meta</c>
/// sets it, else the property's initializer. What an <see cref="IAssetLoader{T}"/> reads its import settings from:
/// <c>file.Metadata.Get&lt;TextureWrap&gt;(nameof(Texture.Wrap))</c>.
/// </summary>
public sealed class AssetMetadata
{
    private readonly ImmutableDictionary<string, object?> _values;

    internal AssetMetadata(ImmutableDictionary<string, object?> values) => _values = values;

    /// <summary>
    /// The value of the marked property <paramref name="property"/>, by its name in C#. Asking for one the type does
    /// not mark, or as another type, is a mistake in the loader, and throws.
    /// </summary>
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
