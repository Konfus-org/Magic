using System.Diagnostics.CodeAnalysis;
using Magic.Assets.Pipeline;

namespace Magic.Assets;

/// <summary>
/// Marks a sealed class that implements this of itself as an asset type, which a <see cref="Handle{T}"/> names and
/// <see cref="AssetManager"/> loads.
/// </summary>
/// <remarks>
/// An asset type holds what its users read, never how its file was read. The <see cref="IAssetImporter{T}"/> that a
/// gem gives for the type reads the file, or Core reads it for a type marked <see cref="AssetFormatAttribute"/>. Every
/// caller that loads an asset gets the same object.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1040:Avoid empty interfaces",
    Justification = "A marker: what makes a type an asset type, which Handle<T> and every load constrain T to."
)]
public interface IAsset<TSelf>
    where TSelf : IAsset<TSelf>;
