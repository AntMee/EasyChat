namespace EasyChat.Contracts.Translation;

public sealed record TranslationMessages(string RequestError);

public static class TranslationPromptDefaults
{
    // Used only when the user has removed every configured prompt.
    public const string DefaultRole =
        "Translate accurately and naturally while preserving meaning, tone, terminology, and formatting.";

    // Kept for source compatibility with callers that used the old name.
    public const string DefaultContent = DefaultRole;
}
