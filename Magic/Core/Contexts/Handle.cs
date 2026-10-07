namespace Magic.Contexts;

/// <summary>
/// An entity of the <see cref="Interfaces.IEcs"/>, by its id; 0 is none. It is only the id, so one kept after its
/// entity was destroyed still holds it: <see cref="Interfaces.IEcs.IsAlive"/> says whether it names an entity now.
/// </summary>
public readonly record struct Handle(ulong Id)
{
    public static Handle None => default;

    public bool IsValid => Id != 0;

    public override string ToString()
    {
        return IsValid ? $"Handle({Id})" : "Handle.Invalid";
    }
}

/// <summary>
/// An asset of type <typeparamref name="T"/>, by its id; 0 is none. What a component or another asset holds instead of
/// a path or the asset itself, written in JSON as <c>{ "id": N }</c>, so the file can move or be renamed.
/// </summary>
public readonly record struct Handle<T>(ulong Id) : ITypedHandle
{
    public static Handle<T> None => default;

    public bool IsValid => Id != 0;

    public override string ToString()
    {
        return IsValid ? $"Handle<{typeof(T).Name}>({Id})" : $"Handle<{typeof(T).Name}>.Invalid";
    }

    Type ITypedHandle.Of => typeof(T);
}

/// <summary>
/// A <see cref="Handle{T}"/> seen without its type argument (boxed, as a walk over an object finds it): its id and its T.
/// </summary>
internal interface ITypedHandle
{
    ulong Id { get; }

    Type Of { get; }
}
