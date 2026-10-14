using Magic.Contexts.Assets;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// What a project's files describe, as the types gems share: a sealed class implementing this, of itself, is an asset
/// type, which a <see cref="Handle{T}"/> names and <see cref="Assets"/> loads. It holds what its users read, never how
/// its file was read, which is the business of the <see cref="IAssetLoader{T}"/> a gem provides for it, or of
/// <see cref="Assets"/> itself for an <see cref="IJsonAsset{TSelf}"/> or an <see cref="ITextAsset{TSelf}"/>. Every caller
/// that loads an asset gets the same object, so nobody changes one once it is loaded.
/// </summary>
public interface IAsset<TSelf>
    where TSelf : IAsset<TSelf>
{
    /// <summary>
    /// The file extensions of the type, matched ignoring case against the end of a file's name, so <c>.hlsl</c> is a
    /// <c>.surf.hlsl</c>'s too: what its files are found by. Its default (<see cref="Assets.Default{T}"/>) is
    /// <c>Default</c> and one of them exactly, tried in order, so the default shader, <c>Default.surf.hlsl</c>, is
    /// found because <c>.surf.hlsl</c> is listed.
    /// </summary>
    static abstract IReadOnlyList<string> Extensions { get; }

    /// <summary>
    /// Which asset this is. <see cref="Assets"/> gives it as it makes the asset; it is never read from the file, and a
    /// loader leaves it as it is.
    /// </summary>
    Handle<TSelf> Handle { get; init; }
}
