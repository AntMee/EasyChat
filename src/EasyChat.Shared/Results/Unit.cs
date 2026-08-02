namespace EasyChat.Shared.Results;

public readonly record struct Unit
{
    public static Unit Value { get; } = new();
}
