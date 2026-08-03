using System.Text.Json;
using EasyChat.Contracts.Speech;

namespace EasyChat.Infrastructure.Speech.EdgeTts;

internal interface IEdgeTtsVoiceCatalog
{
    ValueTask<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<TtsLanguage>> GetLanguagesAsync(CancellationToken cancellationToken);
}

internal sealed class EdgeTtsVoiceCatalog : IEdgeTtsVoiceCatalog
{
    private readonly string _catalogPath;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private Catalog? _catalog;

    public EdgeTtsVoiceCatalog(string assetsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsDirectory);
        _catalogPath = Path.Combine(Path.GetFullPath(assetsDirectory), "voices.json");
    }

    public async ValueTask<IReadOnlyList<TtsVoice>> GetVoicesAsync(
        CancellationToken cancellationToken) =>
        (await LoadAsync(cancellationToken).ConfigureAwait(false)).Voices;

    public async ValueTask<IReadOnlyList<TtsLanguage>> GetLanguagesAsync(
        CancellationToken cancellationToken) =>
        (await LoadAsync(cancellationToken).ConfigureAwait(false)).Languages;

    private async ValueTask<Catalog> LoadAsync(CancellationToken cancellationToken)
    {
        if (_catalog is not null)
            return _catalog;

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_catalog is not null)
                return _catalog;
            await using var stream = File.OpenRead(_catalogPath);
            var definitions = await JsonSerializer.DeserializeAsync<List<VoiceDefinition>>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The Edge TTS voice catalog is empty.");

            var voices = definitions.Select(ToVoice).ToArray();
            var languages = definitions
                .GroupBy(definition => GetLocale(definition.Name), StringComparer.OrdinalIgnoreCase)
                .Select(group => ToLanguage(group.Key, group.First()))
                .OrderBy(language => language.Locale, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _catalog = new Catalog(voices, languages);
            return _catalog;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private static TtsVoice ToVoice(VoiceDefinition definition) => new(
        definition.Name,
        definition.Role,
        GetLocale(definition.Name),
        definition.Gender,
        ParseList(definition.ContentCategories),
        ParseList(definition.VoicePersonalities));

    private static TtsLanguage ToLanguage(string locale, VoiceDefinition definition)
    {
        var separator = definition.EnglishName.IndexOf(" (", StringComparison.Ordinal);
        var language = separator < 0
            ? definition.EnglishName
            : definition.EnglishName[..separator];
        var region = separator < 0
            ? definition.Region
            : definition.EnglishName[(separator + 2)..].TrimEnd(')');
        return new TtsLanguage(
            locale,
            language,
            region,
            definition.EnglishName,
            definition.ChineseName,
            $"{definition.Region.ToLowerInvariant()}.png");
    }

    private static string GetLocale(string name)
    {
        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return "unknown";
        if (name.StartsWith("iu-Cans-CA", StringComparison.Ordinal)
            || name.StartsWith("iu-Latn-CA", StringComparison.Ordinal)
            || name.StartsWith("zh-CN-liaoning", StringComparison.Ordinal)
            || name.StartsWith("zh-CN-shaanxi", StringComparison.Ordinal))
        {
            return string.Join('-', parts.Take(3));
        }

        return $"{parts[0]}-{parts[1]}";
    }

    private static string[] ParseList(string input) =>
        string.IsNullOrWhiteSpace(input)
            ? []
            : input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record Catalog(
        IReadOnlyList<TtsVoice> Voices,
        IReadOnlyList<TtsLanguage> Languages);

    private sealed class VoiceDefinition
    {
        public required string Name { get; init; }
        public required string Gender { get; init; }
        public required string ContentCategories { get; init; }
        public required string VoicePersonalities { get; init; }
        public required string EnglishName { get; init; }
        public required string ChineseName { get; init; }
        public required string Role { get; init; }
        public required string Language { get; init; }
        public required string Region { get; init; }
    }
}
