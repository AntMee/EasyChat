using Newtonsoft.Json;

namespace EasyChat.Infrastructure.Settings.Persistence;

/// <summary>
/// Loads the host-provided prompt catalog used only when the prompt settings file is created.
/// </summary>
internal sealed class BuiltInPromptAssetReader
{
    private const string ChineseFileName = "builtin.prompt.zh.json";
    private const string EnglishFileName = "builtin.prompt.en.json";

    private readonly string _assetsDirectory;
    private readonly ISettingsFileStore _fileStore;

    public BuiltInPromptAssetReader(string assetsDirectory, ISettingsFileStore fileStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsDirectory);
        ArgumentNullException.ThrowIfNull(fileStore);

        _assetsDirectory = Path.GetFullPath(assetsDirectory);
        _fileStore = fileStore;
    }

    public async ValueTask<PromptSettingsDto> ReadAsync(
        string? displayLanguage,
        CancellationToken cancellationToken)
    {
        var fileName = IsChineseDisplayLanguage(displayLanguage)
            ? ChineseFileName
            : EnglishFileName;
        var path = Path.Combine(_assetsDirectory, fileName);
        var json = await _fileStore.ReadAllTextAsync(path, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var definitions = JsonConvert.DeserializeObject<List<BuiltInPromptAssetEntry>>(json,
                              new JsonSerializerSettings { CheckAdditionalContent = true })
                          ?? throw new JsonSerializationException(
                              $"Built-in prompt asset '{fileName}' contains no array.");
        Validate(definitions, fileName);

        var entries = definitions
            .Select(definition => new PromptEntrySettingsDto
            {
                Id = definition.Id,
                Name = definition.Name,
                Content = definition.Content,
                IsDefault = definition.IsDefault
            })
            .ToList();
        var defaultEntry = entries.Single(entry => entry.IsDefault);
        return new PromptSettingsDto
        {
            SelectedPromptId = defaultEntry.Id,
            Entries = entries
        };
    }

    private static void Validate(
        IReadOnlyList<BuiltInPromptAssetEntry> definitions,
        string fileName)
    {
        if (definitions.Count == 0)
            throw new JsonSerializationException(
                $"Built-in prompt asset '{fileName}' must contain at least one prompt.");

        if (definitions.Count(definition => definition.IsDefault) != 1)
            throw new JsonSerializationException(
                $"Built-in prompt asset '{fileName}' must contain exactly one default prompt.");

        if (definitions.Any(definition => string.IsNullOrWhiteSpace(definition.Id)))
            throw new JsonSerializationException(
                $"Built-in prompt asset '{fileName}' contains a prompt without an id.");

        if (definitions.Any(definition => string.IsNullOrWhiteSpace(definition.Name)))
            throw new JsonSerializationException(
                $"Built-in prompt asset '{fileName}' contains a prompt without a name.");

        if (definitions.Any(definition => string.IsNullOrWhiteSpace(definition.Content)))
            throw new JsonSerializationException(
                $"Built-in prompt asset '{fileName}' contains a prompt without content.");

        if (definitions.Select(definition => definition.Id)
            .Distinct(StringComparer.Ordinal)
            .Count() != definitions.Count)
        {
            throw new JsonSerializationException(
                $"Built-in prompt asset '{fileName}' contains duplicate ids.");
        }
    }

    private static bool IsChineseDisplayLanguage(string? displayLanguage) =>
        string.Equals(displayLanguage, "Simplified Chinese", StringComparison.OrdinalIgnoreCase)
        || string.Equals(displayLanguage, "\u7b80\u4f53\u4e2d\u6587", StringComparison.Ordinal)
        || string.Equals(displayLanguage, "zh", StringComparison.OrdinalIgnoreCase)
        || string.Equals(displayLanguage, "zh-Hans", StringComparison.OrdinalIgnoreCase);

    [JsonObject(MemberSerialization.OptIn)]
    private sealed class BuiltInPromptAssetEntry
    {
        [JsonProperty("id", Required = Required.Always)]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("name", Required = Required.Always)]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("content", Required = Required.Always)]
        public string Content { get; set; } = string.Empty;

        [JsonProperty("isDefault", Required = Required.Always)]
        public bool IsDefault { get; set; }
    }
}
