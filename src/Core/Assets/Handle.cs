using System.Diagnostics.CodeAnalysis;

namespace Magic.Assets;

/// <summary>
/// An asset of type <typeparamref name="T"/>, by the id in its <c>.meta</c> sidecar: how a component, another asset or
/// the project names one, written <c>{ "id": 3400 }</c>, so its file can move or be renamed. It is only the id, so a
/// component holding one stays plain unmanaged data, and the type is the compiler's: a texture's cannot be passed where
/// a model's is wanted.
/// </summary>
public readonly record struct Handle<T>(ulong Id)
    where T : IAsset<T>
{
    /// <summary>
    /// No asset: what an empty material slot holds, or a camera drawing into the window.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1000:Do not declare static members on generic types",
        Justification = "Every nothing value is .None, and Handle<Material>.None names the type it is none of."
    )]
    public static Handle<T> None => default;
}
