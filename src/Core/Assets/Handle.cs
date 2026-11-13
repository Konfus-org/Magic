using System.Diagnostics.CodeAnalysis;

namespace Magic.Assets;

/// <summary>
/// Names an asset of type <typeparamref name="T"/> by the id in its <c>.meta</c> sidecar, written
/// <c>{ "id": 3400 }</c>, so that its file can move or get a new name.
/// </summary>
/// <remarks>
/// It holds only the id, so a component that holds one stays plain unmanaged data. The compiler checks the type: a
/// texture's handle cannot go where a model's handle is necessary.
/// </remarks>
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
