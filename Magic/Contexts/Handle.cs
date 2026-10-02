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

public readonly record struct Handle<T>(ulong Id)
{
    public static Handle<T> None { get; } = default;

    public bool IsValid => Id != 0;

    public override string ToString()
    {
        return IsValid ? $"Handle<{typeof(T).Name}>({Id})" : $"Handle<{typeof(T).Name}>.Invalid";
    }
}
