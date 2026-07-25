using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyChat.Lang;
using EasyChat.Models.Configuration;
using ReactiveUI;
using SukiUI.Dialogs;

namespace EasyChat.ViewModels.AiModels;

public class AiModelEditDialogViewModel : ViewModelBase
{
    private readonly CustomAiModel? _existingModel;
    private readonly HttpClient _httpClient = new();
    private bool _isFetchingModels;
    private string _fetchModelsError = string.Empty;

    public AiModelEditDialogViewModel(ISukiDialog dialog, CustomAiModel? existingModel = null)
    {
        var dialog1 = dialog;
        _existingModel = existingModel;

        if (existingModel != null)
        {
            SelectedModelType = existingModel.ModelType;
            Name = existingModel.Name;
            ApiUrl = existingModel.ApiUrl;
            ApiKey = existingModel.ApiKey;
            Model = existingModel.Model;
            UseProxy = existingModel.UseProxy;
            EnableThinking = existingModel.EnableThinking;
        }
        else
        {
            UpdateDefaultsForModelType(AiModelType.OpenAi);
        }

        var canSave = this.WhenAnyValue(
            x => x.ApiUrl,
            x => x.Model,
            x => x.Name,
            x => x.SelectedModelType,
            (url, model, name, type) =>
            {
                if (string.IsNullOrWhiteSpace(url)) return false;
                if (string.IsNullOrWhiteSpace(model)) return false;
                if (type == AiModelType.Custom && string.IsNullOrWhiteSpace(name)) return false;
                return true;
            });

        SaveCommand = ReactiveCommand.Create(() =>
        {
            OnClose?.Invoke(GetResult());
            dialog1.Dismiss();
        }, canSave);

        CancelCommand = ReactiveCommand.Create(() =>
        {
            OnClose?.Invoke(null);
            dialog1.Dismiss();
        });

        var canFetchModels = this.WhenAnyValue(
            x => x.ApiUrl,
            x => x.IsFetchingModels,
            (url, fetching) => !fetching && !string.IsNullOrWhiteSpace(url));
        FetchModelsCommand = ReactiveCommand.CreateFromTask(FetchModelsAsync, canFetchModels);
    }

    public string Title => _existingModel == null ? Resources.AddModel : Resources.EditModel;
    public string ButtonText => _existingModel == null ? Resources.Add : Resources.Save;

    public List<AiModelType> AvailableModelTypes { get; } =
        Enum.GetValues<AiModelType>().ToList();

    public AiModelType SelectedModelType
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            UpdateDefaultsForModelType(value);
            AvailableModels.Clear();
            this.RaisePropertyChanged(nameof(DisplayName));
        }
    } = AiModelType.OpenAi;

    public string DisplayName => SelectedModelType switch
    {
        AiModelType.OpenAi => "OpenAI",
        AiModelType.Gemini => "Gemini",
        AiModelType.Claude => "Claude",
        AiModelType.DeepSeek => "DeepSeek",
        AiModelType.Custom => Resources.CustomModel,
        _ => Resources.Unknown
    };

    public string Name
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public string ApiUrl
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public string ApiKey
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public string Model
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public bool UseProxy
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool EnableThinking
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ObservableCollection<string> AvailableModels { get; } = new();

    public bool IsFetchingModels
    {
        get => _isFetchingModels;
        private set => this.RaiseAndSetIfChanged(ref _isFetchingModels, value);
    }

    public string FetchModelsError
    {
        get => _fetchModelsError;
        private set => this.RaiseAndSetIfChanged(ref _fetchModelsError, value);
    }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> FetchModelsCommand { get; }

    public Action<CustomAiModel?>? OnClose { get; set; }

    private void UpdateDefaultsForModelType(AiModelType modelType)
    {
        switch (modelType)
        {
            case AiModelType.OpenAi:
                ApiUrl = "https://api.openai.com/v1";
                if (string.IsNullOrEmpty(Model))
                    Model = "gpt-4o";
                Name = "OpenAI";
                break;
            case AiModelType.Gemini:
                ApiUrl = "https://generativelanguage.googleapis.com/v1beta/openai/";
                if (string.IsNullOrEmpty(Model))
                    Model = "gemini-pro";
                Name = "Gemini";
                break;
            case AiModelType.Claude:
                ApiUrl = "https://api.anthropic.com/v1/";
                if (string.IsNullOrEmpty(Model))
                    Model = "claude-3-opus-20240229";
                Name = "Claude";
                break;
            case AiModelType.DeepSeek:
                ApiUrl = "https://api.deepseek.com/v1";
                if (string.IsNullOrEmpty(Model))
                    Model = "deepseek-chat";
                Name = "DeepSeek";
                break;
            case AiModelType.Custom:
                ApiUrl = "https://api.openai.com/v1";
                Model = "";
                Name = "";
                break;
        }
    }

    public CustomAiModel GetResult()
    {
        return new CustomAiModel
        {
            Id = _existingModel?.Id ?? Guid.NewGuid().ToString(),
            Name = string.IsNullOrWhiteSpace(Name) ? DisplayName : Name,
            ModelType = SelectedModelType,
            ApiKeys = _existingModel == null
                ? new ObservableCollection<string>()
                : new ObservableCollection<string>(_existingModel.ApiKeys),
            ApiKey = ApiKey,
            ApiUrl = ApiUrl,
            Model = Model,
            UseProxy = UseProxy,
            EnableThinking = EnableThinking
        };
    }

    private async Task FetchModelsAsync()
    {
        FetchModelsError = string.Empty;
        if (string.IsNullOrWhiteSpace(ApiUrl))
        {
            FetchModelsError = Resources.ModelApiUrlRequired;
            return;
        }

        IsFetchingModels = true;
        try
        {
            var apiBase = ApiUrl.TrimEnd('/');
            if (SelectedModelType == AiModelType.Gemini &&
                apiBase.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
            {
                apiBase = apiBase[..^"/openai".Length];
            }

            var endpoint = $"{apiBase}/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (SelectedModelType == AiModelType.Gemini && !string.IsNullOrWhiteSpace(ApiKey))
            {
                request.RequestUri = new Uri($"{endpoint}?key={Uri.EscapeDataString(ApiKey)}");
            }
            else if (SelectedModelType == AiModelType.Claude && !string.IsNullOrWhiteSpace(ApiKey))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", ApiKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
            else if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            }

            using var response = await _httpClient.SendAsync(request, CancellationToken.None);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var ids = ExtractModelIds(document.RootElement)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            AvailableModels.Clear();
            foreach (var id in ids) AvailableModels.Add(id);
            if (ids.Count == 0) FetchModelsError = Resources.NoModelsFound;
        }
        catch (Exception ex)
        {
            FetchModelsError = string.Format(Resources.FetchModelsFailed, ex.Message);
        }
        finally
        {
            IsFetchingModels = false;
        }
    }

    private static IEnumerable<string> ExtractModelIds(JsonElement root)
    {
        var models = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
            ? data
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var providerModels)
                ? providerModels
                : root;
        if (models.ValueKind != JsonValueKind.Array) yield break;

        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var id = item.GetString();
                if (!string.IsNullOrWhiteSpace(id)) yield return id;
            }
            else if (item.ValueKind == JsonValueKind.Object &&
                     (item.TryGetProperty("id", out var idProperty) || item.TryGetProperty("name", out idProperty)))
            {
                var id = idProperty.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                    yield return id.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                        ? id["models/".Length..]
                        : id;
            }
        }
    }
}
