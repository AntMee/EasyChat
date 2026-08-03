using System.Collections.ObjectModel;
using System.Reactive;
using EasyChat.Contracts.AiModels;
using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Foundation.Navigation;
using EasyChat.Presentation.Lang;
using ReactiveUI;
using SukiUI.Dialogs;

namespace EasyChat.Presentation.Features.Settings.Translation
{
    public sealed class AiModelEditDialogViewModel : ConventionViewModelBase
    {
        private readonly ISukiDialog _dialog;
        private readonly IAiModelCatalogTransport _catalog;
        private readonly CustomAiModelState? _existing;
        private bool _isFetchingModels;
        private string _fetchModelsError = string.Empty;
        private AiModelType _selectedModelType = AiModelType.OpenAi;
        private string _name = string.Empty;
        private string _apiUrl = string.Empty;
        private string _apiKey = string.Empty;
        private string _model = string.Empty;
        private bool _useProxy;
        private bool _enableThinking;

        public AiModelEditDialogViewModel(
            ISukiDialog dialog,
            IAiModelCatalogTransport catalog,
            CustomAiModelState? existing = null)
        {
            _dialog = dialog;
            _catalog = catalog;
            _existing = existing;
            if (existing is null)
            {
                UpdateDefaults(AiModelType.OpenAi);
            }
            else
            {
                _selectedModelType = existing.ModelType;
                _name = existing.Name;
                _apiUrl = existing.ApiUrl;
                _apiKey = existing.ApiKey;
                _model = existing.Model;
                _useProxy = existing.UseProxy;
                _enableThinking = existing.EnableThinking;
                if (!string.IsNullOrWhiteSpace(_model))
                    AvailableModels.Add(_model);
            }

            var canSave = this.WhenAnyValue(
                viewModel => viewModel.ApiUrl,
                viewModel => viewModel.Model,
                viewModel => viewModel.Name,
                viewModel => viewModel.SelectedModelType,
                (url, model, name, type) =>
                    !string.IsNullOrWhiteSpace(url) &&
                    !string.IsNullOrWhiteSpace(model) &&
                    (type != AiModelType.Custom || !string.IsNullOrWhiteSpace(name)));
            SaveCommand = ReactiveCommand.Create(Save, canSave);
            CancelCommand = ReactiveCommand.Create(Cancel);
            FetchModelsCommand = ReactiveCommand.CreateFromTask(
                FetchModelsAsync,
                this.WhenAnyValue(
                    viewModel => viewModel.ApiUrl,
                    viewModel => viewModel.IsFetchingModels,
                    (url, fetching) => !fetching && !string.IsNullOrWhiteSpace(url)));
        }

        public string ButtonText => _existing is null ? Resources.Add : Resources.Save;
        public List<AiModelType> AvailableModelTypes { get; } = Enum.GetValues<AiModelType>().ToList();
        public ObservableCollection<string> AvailableModels { get; } = [];

        public AiModelType SelectedModelType
        {
            get => _selectedModelType;
            set
            {
                if (_selectedModelType == value)
                    return;
                this.RaiseAndSetIfChanged(ref _selectedModelType, value);
                UpdateDefaults(value);
                AvailableModels.Clear();
                this.RaisePropertyChanged(nameof(DisplayName));
            }
        }

        public string DisplayName => SelectedModelType switch
        {
            AiModelType.OpenAi => "OpenAI",
            AiModelType.Gemini => "Gemini",
            AiModelType.Claude => "Claude",
            AiModelType.DeepSeek => "DeepSeek",
            AiModelType.Custom => Resources.CustomModel,
            _ => Resources.Unknown
        };

        public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }
        public string ApiUrl { get => _apiUrl; set => this.RaiseAndSetIfChanged(ref _apiUrl, value); }
        public string ApiKey { get => _apiKey; set => this.RaiseAndSetIfChanged(ref _apiKey, value); }
        public string Model { get => _model; set => this.RaiseAndSetIfChanged(ref _model, value); }
        public bool UseProxy { get => _useProxy; set => this.RaiseAndSetIfChanged(ref _useProxy, value); }
        public bool EnableThinking { get => _enableThinking; set => this.RaiseAndSetIfChanged(ref _enableThinking, value); }
        public bool IsFetchingModels { get => _isFetchingModels; private set => this.RaiseAndSetIfChanged(ref _isFetchingModels, value); }
        public string FetchModelsError { get => _fetchModelsError; private set => this.RaiseAndSetIfChanged(ref _fetchModelsError, value); }
        public ReactiveCommand<Unit, Unit> SaveCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }
        public ReactiveCommand<Unit, Unit> FetchModelsCommand { get; }
        public Action<CustomAiModelSettings?>? OnClose { get; init; }

        private void Save()
        {
            var keys = _existing?.ApiKeys.ToList() ?? [];
            if (keys.Count == 0)
                keys.Add(ApiKey);
            else
                keys[0] = ApiKey;
            OnClose?.Invoke(new CustomAiModelSettings(
                _existing?.Id ?? Guid.NewGuid().ToString(),
                string.IsNullOrWhiteSpace(Name) ? DisplayName : Name,
                SelectedModelType,
                keys.Where(key => !string.IsNullOrWhiteSpace(key)).ToArray(),
                ApiUrl,
                Model,
                UseProxy,
                EnableThinking));
            _dialog.Dismiss();
        }

        private void Cancel()
        {
            OnClose?.Invoke(null);
            _dialog.Dismiss();
        }

        private async Task FetchModelsAsync()
        {
            FetchModelsError = string.Empty;
            IsFetchingModels = true;
            try
            {
                var provider = SelectedModelType switch
                {
                    AiModelType.Gemini => AiModelCatalogProvider.Gemini,
                    AiModelType.Claude => AiModelCatalogProvider.Claude,
                    _ => AiModelCatalogProvider.OpenAiCompatible
                };
                var models = await _catalog.FetchModelsAsync(new AiModelCatalogRequest(ApiUrl, ApiKey, provider));
                AvailableModels.Clear();
                foreach (var availableModel in models)
                    AvailableModels.Add(availableModel);
                if (models.Count == 0)
                    FetchModelsError = Resources.NoModelsFound;
                else if (string.IsNullOrWhiteSpace(Model) || !models.Contains(Model, StringComparer.OrdinalIgnoreCase))
                    Model = models[0];
            }
            catch (Exception exception)
            {
                FetchModelsError = string.Format(Resources.FetchModelsFailed, exception.Message);
            }
            finally
            {
                IsFetchingModels = false;
            }
        }

        private void UpdateDefaults(AiModelType type)
        {
            (ApiUrl, Model, Name) = type switch
            {
                AiModelType.OpenAi => ("https://api.openai.com/v1", "gpt-4o", "OpenAI"),
                AiModelType.Gemini => ("https://generativelanguage.googleapis.com/v1beta/openai/", "gemini-pro", "Gemini"),
                AiModelType.Claude => ("https://api.anthropic.com/v1/", "claude-3-opus-20240229", "Claude"),
                AiModelType.DeepSeek => ("https://api.deepseek.com/v1", "deepseek-chat", "DeepSeek"),
                _ => ("https://api.openai.com/v1", string.Empty, string.Empty)
            };
        }
    }
}

namespace EasyChat.Presentation.Features.Settings.Translation
{
    public enum KeyListType
    {
        String,
        Baidu,
        Tencent
    }

    public abstract class KeyItemViewModelBase : ReactiveObject;

    public sealed class StringKeyItemViewModel : KeyItemViewModelBase
    {
        private string _value = string.Empty;
        public string Value { get => _value; set => this.RaiseAndSetIfChanged(ref _value, value); }
    }

    public sealed class BaiduKeyItemViewModel : KeyItemViewModelBase
    {
        private string _appId = string.Empty;
        private string _appKey = string.Empty;
        public string AppId { get => _appId; set => this.RaiseAndSetIfChanged(ref _appId, value); }
        public string AppKey { get => _appKey; set => this.RaiseAndSetIfChanged(ref _appKey, value); }
    }

    public sealed class TencentKeyItemViewModel : KeyItemViewModelBase
    {
        private string _secretId = string.Empty;
        private string _secretKey = string.Empty;
        public string SecretId { get => _secretId; set => this.RaiseAndSetIfChanged(ref _secretId, value); }
        public string SecretKey { get => _secretKey; set => this.RaiseAndSetIfChanged(ref _secretKey, value); }
    }

    public sealed class KeyListEditorViewModel : ConventionViewModelBase
    {
        private readonly ISukiDialog _dialog;
        private readonly KeyListType _type;

        public KeyListEditorViewModel(
            ISukiDialog dialog,
            string title,
            KeyListType type,
            IEnumerable<KeyItemViewModelBase> items)
        {
            _dialog = dialog;
            _type = type;
            Title = title;
            Items = new ObservableCollection<KeyItemViewModelBase>(items);
            AddCommand = ReactiveCommand.Create(Add);
            RemoveCommand = ReactiveCommand.Create<KeyItemViewModelBase>(item => Items.Remove(item));
            SaveCommand = ReactiveCommand.Create(Save);
            CancelCommand = ReactiveCommand.Create(dialog.Dismiss);
        }

        public string Title { get; }
        public ObservableCollection<KeyItemViewModelBase> Items { get; }
        public ReactiveCommand<Unit, Unit> AddCommand { get; }
        public ReactiveCommand<KeyItemViewModelBase, Unit> RemoveCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }
        public Action<IReadOnlyList<KeyItemViewModelBase>>? OnSave { get; init; }

        private void Add() => Items.Add(_type switch
        {
            KeyListType.Baidu => new BaiduKeyItemViewModel(),
            KeyListType.Tencent => new TencentKeyItemViewModel(),
            _ => new StringKeyItemViewModel()
        });

        private void Save()
        {
            OnSave?.Invoke(Items);
            _dialog.Dismiss();
        }
    }
}
