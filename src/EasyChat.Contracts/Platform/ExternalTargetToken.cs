namespace EasyChat.Contracts.Platform;

public readonly record struct ExternalTargetToken(string Value)
{
    public static ExternalTargetToken None { get; } = new(string.Empty);

    public bool IsEmpty => string.IsNullOrEmpty(Value);
}
