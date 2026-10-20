using System.Text.Json;
using Magic.Contexts.Utils;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// Reads the files of one asset type: a file format, turning what is on disk into what the engine works with. A gem
/// provides one for its format as it provides any interface, by implementing it, and <see cref="Assets"/> asks for the
/// one of a type (through <see cref="IServiceProvider"/>) each time it reads a file of the type. A type no gem provides
/// one for is read as JSON when it is an <see cref="IJsonAsset{TSelf}"/>, and as text when it is an
/// <see cref="ITextAsset{TSelf}"/>.
/// </summary>
public interface IAssetLoader<T>
    where T : IAsset<T>
{
    /// <summary>
    /// The asset in <paramref name="bytes"/>, the file at asset path <paramref name="path"/> (for its extension and for
    /// messages), or why it is not one, read with the import settings its <c>.meta</c> sidecar holds beside the id,
    /// <paramref name="settings"/>, which the loader reads as a type of its own
    /// (<c>Json.Read&lt;TextureMeta&gt;(settings)</c>), so no loader handles the sidecar's format. A loader may load the
    /// assets one is made from through <see cref="Assets"/>, as a prefab loads the prefabs it places: they are what it is
    /// made from (<see cref="Assets.GetDependencies{T}"/>), so it is made again when one of them changes. The asset's
    /// <see cref="IAsset{TSelf}.Handle"/> is left as it is: <see cref="Assets"/> gives it. On workers, for several files
    /// at once.
    /// </summary>
    ValueTask<Result<T>> LoadAsync(string path, ReadOnlyMemory<byte> bytes, JsonElement settings, CancellationToken cancel);
}
