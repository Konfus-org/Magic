using System.Diagnostics.CodeAnalysis;

namespace Magic.State;

/// <summary>
/// The outcome of work that can fail on content or the machine and has nothing to give back (writing, deleting): done,
/// or why not. It also makes <see cref="Result{T}"/> values: <see cref="Success{T}(T)"/> takes the type from the
/// value, and <see cref="Failure(string)"/> becomes whichever result the method returns.
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

    public static implicit operator Result(Failure failure) => new(false, failure.Error);

    public static Result Success() => new(true, string.Empty);

    public static Result<T> Success<T>(T value) => new(true, value, string.Empty);

    public static Failure Failure(string error) => new(error);
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

    public static implicit operator Result(Result<T> result) =>
        result.Ok ? Result.Success() : Result.Failure(result.Error);

    public static implicit operator Result<T>(Failure failure) => new(false, default, failure.Error);
}

/// <summary>
/// Why work failed, before it is a result. It becomes a <see cref="Result"/> or any <see cref="Result{T}"/>, so
/// returning one needs no type argument.
/// </summary>
public readonly record struct Failure(string Error);
