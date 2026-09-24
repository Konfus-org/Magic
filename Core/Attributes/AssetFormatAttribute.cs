namespace Magic.Attributes;

public enum AssetFormat
{
    /// <summary>The file is the asset class as JSON (see <see cref="Contexts.Assets.AssetJson"/>).</summary>
    Json,

    /// <summary>The file is text; it goes into the class's <c>public string Text { get; set; }</c>.</summary>
    Text
}

/// <summary>
/// Marks an asset type the host can read by itself, without a loader gem. Without this attribute a gem must
/// export an <see cref="Interfaces.IAssetLoader{T}"/> for the type. Either way the <c>.meta</c> sidecar is
/// deserialised as the asset type first.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AssetFormatAttribute(AssetFormat format) : Attribute
{
    public AssetFormat Format { get; } = format;
}
