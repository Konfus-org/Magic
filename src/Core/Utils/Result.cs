using System.Diagnostics.CodeAnalysis;

namespace Magic.Utils;

/// <summary>
/// The outcome of work that can fail on content or the machine and gives nothing back, such as a write: done, or why
/// not.
/// </summary>
/// <remarks>
/// It also makes <see cref="Result{T}"/> values: <see cref="Success{T}(T)"/> takes the type from the value, and
/// <see cref="Failure(string)"/> becomes the result type that the method returns.
/// </remarks>
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

    /// <summary>
    /// The first of <paramref name="results"/> that failed, or success when none did: one check of work done in pieces.
    /// </summary>
    public static Result FindFailure(params ReadOnlySpan<Result> results)
    {
        foreach (Result result in results)
        {
            if (!result.Ok)
                return result;
        }

        return Success();
    }
}

/// <summary>
/// The outcome of work that can fail on content or the machine, such as a read: the value, or why there is none.
/// </summary>
/// <remarks>
/// Read <see cref="Value"/> only when <see cref="Ok"/> is true.
/// </remarks>
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
