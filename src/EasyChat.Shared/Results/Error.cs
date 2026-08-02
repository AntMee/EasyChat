namespace EasyChat.Shared.Results;

public sealed record Error(string Code, string Message)
{
    public static Error None { get; } = new(string.Empty, string.Empty);

    public bool IsNone => string.IsNullOrEmpty(Code);
}
