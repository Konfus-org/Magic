namespace Magic.Utils;

public class Result(bool isSuccess, string message = "")
{
    public bool IsSuccess { get; } = isSuccess;
    public string Message { get; } = message;

    public static Result Success()
    {
        return new Result(true);
    }

    public static Result Failure(string message)
    {
        return new Result(false, message);
    }
}

public class Result<T>(T payload, bool isSuccess, string message = "") : Result(isSuccess, message)
{
    public T Payload { get; } = payload;

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
