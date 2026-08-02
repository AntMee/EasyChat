namespace EasyChat.Shared.Results;

public readonly record struct OperationError(string Code, string Message)
{
    public static OperationError None { get; } = new(string.Empty, string.Empty);
}

public readonly record struct OperationResult<T>
{
    private readonly T? _value;

    private OperationResult(T value)
    {
        IsSuccess = true;
        _value = value;
        Error = OperationError.None;
    }

    private OperationResult(OperationError error)
    {
        IsSuccess = false;
        _value = default;
        Error = error;
    }

    public bool IsSuccess { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result has no value.");

    public OperationError Error { get; }

    public static OperationResult<T> Success(T value) => new(value);

    public static OperationResult<T> Failure(string code, string message) =>
        new(new OperationError(code, message));
}

