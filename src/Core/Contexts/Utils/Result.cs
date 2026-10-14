using System.Diagnostics.CodeAnalysis;

namespace Magic.Contexts.Utils;

/// <summary>
/// The outcome of work that can fail on content or the machine and has nothing to give back (writing, deleting): done,
/// or why not. It also makes <see cref="Result{T}"/> values, the type taken from the value.
/// </summary>
public readonly record struct Result
{
    private Result(bool ok, string error)
    {
        Ok = ok;
        Error = error;
    }

    public bool Ok { get; }

    /// <summary>
    /// Why it failed, written for the log; empty on success.
    /// </summary>
    public string Error { get; }

    public static Result Success() => new(true, string.Empty);

    public static Result Failure(string error) => new(false, error);

    public static Result<T> Success<T>(T value) => new(true, value, string.Empty);

    public static Result<T> Failure<T>(string error) => new(false, default, error);
}

/// <summary>
/// The outcome of work that can fail on content or the machine (reading, parsing, loading): the value, or why
/// there is none. <see cref="Value"/> is there only once <see cref="Ok"/> says so.
/// </summary>
public readonly record struct Result<T>
{
    internal Result(bool ok, T? value, string error)
    {
        Ok = ok;
        Value = value;
        Error = error;
    }

    [MemberNotNullWhen(true, nameof(Value))]
    public bool Ok { get; }

    public T? Value { get; }

    /// <summary>
    /// Why it failed, written for the log; empty on success.
    /// </summary>
    public string Error { get; }
}
