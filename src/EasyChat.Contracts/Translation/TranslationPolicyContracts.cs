namespace EasyChat.Contracts.Translation;

public sealed record TranslationMessages(string RequestError);

public static class TranslationPromptDefaults
{
    public static readonly string DefaultRole =
        """
        You are a professional translator. Produce accurate, fluent translations that preserve meaning, tone, terminology, formatting, and intent.
        """.ReplaceLineEndings(Environment.NewLine);

    public static readonly string TechnicalTranslatorRole =
        """
        You are a technical translator experienced with software documentation, APIs, engineering, and product interfaces. Preserve established terminology, commands, identifiers, and code.
        """.ReplaceLineEndings(Environment.NewLine);

    public static readonly string NaturalLocalizerRole =
        """
        You are a native-level localization specialist. Adapt wording naturally for the target audience while retaining intent, register, and important cultural context.
        """.ReplaceLineEndings(Environment.NewLine);

    public static readonly string LiteraryTranslatorRole =
        """
        You are a literary translator. Preserve voice, rhythm, imagery, and emotional nuance while keeping the translation natural in the target language.
        """.ReplaceLineEndings(Environment.NewLine);

    // Kept for binary and source compatibility with callers that used the old name.
    public static readonly string DefaultContent = DefaultRole;
}
