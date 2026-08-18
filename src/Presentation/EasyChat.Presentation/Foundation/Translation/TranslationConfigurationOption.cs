using EasyChat.Contracts.Settings;
using Material.Icons;

namespace EasyChat.Presentation.Foundation.Translation;

public sealed record TranslationConfigurationOption(
    string Id,
    string Name,
    bool IsGlobal,
    MaterialIconKind Icon)
{
    public object? ImageValue { get; init; }

    public const string FollowGlobalId = TranslationConfigurationOptionIds.FollowGlobal;

    public static TranslationConfigurationOption FollowGlobal(string name) =>
        new(FollowGlobalId, name, true, MaterialIconKind.LinkVariant);
}
