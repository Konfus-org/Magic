namespace Magic.Contexts;

public readonly record struct Handle(ulong Id)
{
    public static Handle None { get; } = default;

    public bool IsValid => Id != 0;

    public override string ToString()
    {
        return IsValid ? $"Handle({Id})" : "Handle.Invalid";
    }
}

public readonly record struct Handle<T>(ulong Id) : ITypedHandle
{
    public static Handle<T> None { get; } = default;

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
