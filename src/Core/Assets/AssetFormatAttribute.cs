using Magic.Assets.Pipeline;

namespace Magic.Assets;

/// <summary>
/// How Core reads an asset type's files itself, whatever their extension.
/// </summary>
public enum AssetFileFormat : byte
{
    /// <summary>
    /// The file is the type as JSON, in Core's one dialect, with what its <c>.meta</c> sets over it.
    /// </summary>
    Json,

    /// <summary>
    /// The file is text, set as the type's <c>Text</c> string, with what its <c>.meta</c> sets beside it. The text is
    /// without a byte order mark.
    /// </summary>
    Text,
}

/// <summary>
/// Has Core read an asset type's files itself, in <see cref="Format"/>. A type without it is read by the
/// <see cref="IAssetLoader{T}"/> a gem provides for it.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AssetFormatAttribute(AssetFileFormat format) : Attribute
{
    public AssetFileFormat Format { get; } = format;
}
