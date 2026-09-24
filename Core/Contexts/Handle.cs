namespace Magic.Contexts;

public readonly record struct Handle(ulong Id)
{
    public static readonly Handle None = default;

    public bool IsValid => Id != 0;

    public override string ToString()
    {
        return !IsValid ? "Handle.Invalid" : $"Handle({Id})";
    }
}

public readonly record struct Handle<T>(ulong Id)
{
    public static readonly Handle<T> None = default;

    public bool IsValid => Id != 0;

    public override string ToString()
    {
        return !IsValid ? $"Handle<{typeof(T).Name}>.Invalid" : $"Handle<{typeof(T).Name}>({Id})";
    }
}
