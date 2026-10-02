namespace Magic.Utils;

/// <summary>Whether something worked, and why not when it did not.</summary>
public readonly record struct Result(string? Error)
{
    public bool Ok => Error is null;

    public bool Failed => Error is not null;

    public string Message => Error ?? "";

    public static Result Success()
    {
        return new Result(null);
    }

    public static Result Failure(string message)
    {
        return new Result(message);
    }
}

/// <summary>A value, or why there is none.</summary>
public readonly record struct Result<T>(T Payload, string? Error)
{
    public bool Ok => Error is null;

    public bool Failed => Error is not null;

    public string Message => Error ?? "";

    public static Result<T> Success(T value)
    {
        return new Result<T>(value, null);
    }

    public static Result<T> Failure(string message)
    {
        return new Result<T>(default!, message);
    }
}
