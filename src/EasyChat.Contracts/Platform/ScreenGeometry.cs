namespace EasyChat.Contracts.Platform;

public readonly record struct ScreenId(string Value);

public readonly record struct ScreenPoint(int X, int Y);

public readonly record struct ScreenRegion(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public sealed record ScreenDescriptor(
    ScreenId Id,
    ScreenRegion Bounds,
    double DpiX,
    double DpiY,
    bool IsPrimary);
