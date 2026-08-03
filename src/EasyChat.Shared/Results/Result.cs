namespace EasyChat.Shared.Results;

public readonly struct Result
{
    private Result(bool isSuccess, Error error)
    {
        if (isSuccess == !error.IsNone)
            throw new ArgumentException("A successful result cannot contain an error, and a failed result requires one.");

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(false, error);
    }
}
