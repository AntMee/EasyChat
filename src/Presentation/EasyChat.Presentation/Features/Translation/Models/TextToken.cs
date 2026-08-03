namespace EasyChat.Presentation.Features.Translation.Models;

public sealed record TextToken(string Text, bool IsWord, int StartIndex, int Length);
