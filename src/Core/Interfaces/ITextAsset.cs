namespace Magic.Interfaces;

/// <summary>
/// An asset type whose file is UTF-8 text, kept as it is in <see cref="Text"/>: Core's <see cref="IAssetLoader{T}"/>
/// reads it, making the type with its parameterless constructor, so the class is all there is to write, and its
/// <c>.meta</c> sidecar holds only the id.
/// </summary>
public interface ITextAsset<TSelf> : IAsset<TSelf>
    where TSelf : IAsset<TSelf>
{
    /// <summary>
    /// The file's text, without a byte order mark. Core's loader gives it as it makes the asset.
    /// </summary>
    string Text { get; init; }
}
