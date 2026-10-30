using System.Diagnostics.CodeAnalysis;

namespace Magic.Assets;

/// <summary>
/// What a project's files describe, as the types gems share: a sealed class implementing this, of itself, is an asset
/// type, which a <see cref="Handle{T}"/> names and <see cref="AssetManager"/> loads. It holds what its users read, never
/// how its file was read, which is the business of the <see cref="IAssetLoader{T}"/> a gem provides for it, or of Core
/// itself for a type marked <see cref="AssetFormatAttribute"/>. Every caller that loads an asset gets the same object.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1040:Avoid empty interfaces",
    Justification = "A marker: what makes a type an asset type, which Handle<T> and every load constrain T to."
)]
public interface IAsset<TSelf>
    where TSelf : IAsset<TSelf>;
