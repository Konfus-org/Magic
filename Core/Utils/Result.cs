namespace Magic.Utils;

public class Result
{
    public bool IsSuccess { get; }
    public string Message { get; } = string.Empty;

    public Result(bool isSuccess, string message = "")
    {
        IsSuccess = isSuccess;
        Message = message;
    }

    public static Result Success()
    {
        return new Result(true);
    }

    public static Result Failure(string message)
    {
        return new Result(false, message);
    }
}

public class Result<T> : Result
{
    public Result(T payload, bool isSuccess, string message = "")
        : base(isSuccess, message)
    {
        Payload = payload;
    }

    public T Payload { get; }

    public bool Ok => IsSuccess;
    public bool Failed => !IsSuccess;

    public static Result<T> Success(T value)
    {
        return new Result<T>(value, true);
    }

    public static new Result<T> Failure(string message)
    {
        return new Result<T>(default!, false, message);
    }
}
