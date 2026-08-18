using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using EasyChat.Contracts.AiModels;
using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Foundation.Formatting;
using EasyChat.Presentation.Foundation.Navigation;
using EasyChat.Presentation.Lang;
using EasyChat.Presentation.Foundation.UiHost;
using ReactiveUI;
using ShadUI;

namespace EasyChat.Presentation.Features.Settings.Translation
{
    public sealed class AiModelEditDialogViewModel : ConventionViewModelBase
    {
        private readonly DialogManager _dialogManager;
        private readonly IAiModelCatalogTransport _catalog;
        private readonly SettingsSession _settings;
        private readonly ToastManager _toasts;
        private readonly CustomAiModelState? _existing;
        private CancellationTokenSource? _scheduledModelFetch;
        private CancellationTokenSource? _activeModelFetch;
        private bool _hasInitialized;
        private bool _isFetchingModels;
        private bool _isModelConfirmationRequired;
        private string _fetchModelsError = string.Empty;
        private string _modelConfirmationMessage = string.Empty;
        private AiModelType _selectedModelType = AiModelType.OpenAi;
        private string _name = string.Empty;
        private string _apiUrl = string.Empty;
        private string _apiKey = string.Empty;
        private string _model = string.Empty;
        private bool _useProxy;
        private bool _enableThinking;

        public AiModelEditDialogViewModel(
            DialogManager dialogManager,
            IAiModelCatalogTransport catalog,
            SettingsSession settings,
            ToastManager toasts,
            CustomAiModelState? existing = null)
        {
            _dialogManager = dialogManager;
            _catalog = catalog;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
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
            }

            var canSave = this.WhenAnyValue(
                viewModel => viewModel.ApiUrl,
                viewModel => viewModel.Model,
                viewModel => viewModel.Name,
                viewModel => viewModel.SelectedModelType,
                viewModel => viewModel.IsFetchingModels,
                (url, model, name, type, isFetching) =>
                    !isFetching &&
                    !string.IsNullOrWhiteSpace(url) &&
                    !string.IsNullOrWhiteSpace(model) &&
                    (type != AiModelType.Custom || !string.IsNullOrWhiteSpace(name)));
            SaveCommand = ReactiveCommand.Create(RequestSave, canSave);
            ConfirmSaveCommand = ReactiveCommand.Create(Save, canSave);
            CancelCommand = ReactiveCommand.Create(Cancel);
            FetchModelsCommand = ReactiveCommand.CreateFromTask(
                FetchModelsManuallyAsync,
                this.WhenAnyValue(
                    viewModel => viewModel.ApiUrl,
                    viewModel => viewModel.IsFetchingModels,
                    (url, fetching) => !fetching && !string.IsNullOrWhiteSpace(url)));
        }

        public string Title => _existing is null ? Resources.AddModel : Resources.EditModel;
        public string ButtonText => _existing is null ? Resources.Add : Resources.Save;
        public List<AiModelType> AvailableModelTypes { get; } = Enum
            .GetValues<AiModelType>()
            .OrderBy(type => type == AiModelType.Custom)
            .ToList();
        public ObservableCollection<string> AvailableModels { get; } = [];

        public AiModelType SelectedModelType
        {
            get => _selectedModelType;
            set
            {
                if (_selectedModelType == value)
                    return;
                this.RaiseAndSetIfChanged(ref _selectedModelType, value);
                CancelModelFetches();
                UpdateDefaults(value);
                AvailableModels.Clear();
                ResetModelConfirmation();
                this.RaisePropertyChanged(nameof(DisplayName));
            }
        }

        public string DisplayName => AiModelTypeConverters.GetDisplayName(SelectedModelType);

        public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }
        public string ApiUrl
        {
            get => _apiUrl;
            set
            {
                if (string.Equals(_apiUrl, value, StringComparison.Ordinal))
                    return;
                this.RaiseAndSetIfChanged(ref _apiUrl, value);
                OnCatalogCredentialsChanged(scheduleFetch: false);
            }
        }
        public string ApiKey
        {
            get => _apiKey;
            set
            {
                if (string.Equals(_apiKey, value, StringComparison.Ordinal))
                    return;
                this.RaiseAndSetIfChanged(ref _apiKey, value);
                OnCatalogCredentialsChanged(scheduleFetch: true);
            }
        }
        public string Model
        {
            get => _model;
            set
            {
                if (string.Equals(_model, value, StringComparison.Ordinal))
                    return;
                this.RaiseAndSetIfChanged(ref _model, value);
                ResetModelConfirmation();
            }
        }
        public bool UseProxy
        {
            get => _useProxy;
            set
            {
                if (value && !HasConfiguredNetworkProxy())
                {
                    ShowNetworkProxyRequired();
                    if (_useProxy)
                        this.RaiseAndSetIfChanged(ref _useProxy, false);
                    else
                        this.RaisePropertyChanged(nameof(UseProxy));
                    Dispatcher.UIThread.Post(
                        () => this.RaisePropertyChanged(nameof(UseProxy)),
                        DispatcherPriority.Background);
                    return;
                }

                this.RaiseAndSetIfChanged(ref _useProxy, value);
            }
        }
        public bool EnableThinking { get => _enableThinking; set => this.RaiseAndSetIfChanged(ref _enableThinking, value); }
        public bool IsFetchingModels { get => _isFetchingModels; private set => this.RaiseAndSetIfChanged(ref _isFetchingModels, value); }
        public string FetchModelsError { get => _fetchModelsError; private set => this.RaiseAndSetIfChanged(ref _fetchModelsError, value); }
        public bool IsModelConfirmationRequired
        {
            get => _isModelConfirmationRequired;
            private set
            {
                this.RaiseAndSetIfChanged(ref _isModelConfirmationRequired, value);
                this.RaisePropertyChanged(nameof(IsModelConfirmationNotRequired));
            }
        }
        public bool IsModelConfirmationNotRequired => !IsModelConfirmationRequired;
        public string ModelConfirmationMessage
        {
            get => _modelConfirmationMessage;
            private set => this.RaiseAndSetIfChanged(ref _modelConfirmationMessage, value);
        }
        public ReactiveCommand<Unit, Unit> SaveCommand { get; }
        public ReactiveCommand<Unit, Unit> ConfirmSaveCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }
        public ReactiveCommand<Unit, Unit> FetchModelsCommand { get; }
        public Action<CustomAiModelSettings?>? OnClose { get; init; }

        public Task InitializeAsync()
        {
            if (_hasInitialized || _existing is null)
                return Task.CompletedTask;

            _hasInitialized = true;
            return FetchModelsAsync(showErrors: true);
        }

        private void RequestSave()
        {
            if (AvailableModels.Contains(Model, StringComparer.OrdinalIgnoreCase))
            {
                Save();
                return;
            }

            ModelConfirmationMessage = string.Format(Resources.ModelNotInListConfirmation, Model);
            IsModelConfirmationRequired = true;
        }

        private void Save()
        {
            if (UseProxy && !HasConfiguredNetworkProxy())
            {
                ShowNetworkProxyRequired();
                UseProxy = false;
                return;
            }

            CancelModelFetches();
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
            _dialogManager.Close(this);
        }

        private bool HasConfiguredNetworkProxy() => _settings.Proxy.Mode switch
        {
            NetworkProxyMode.System => true,
            NetworkProxyMode.Custom => Uri.TryCreate(_settings.Proxy.ProxyUrl, UriKind.Absolute, out _),
            _ => false
        };

        private void ShowNetworkProxyRequired() => _toasts
            .CreateToast(Resources.NetworkProxy)
            .WithContent(Resources.NetworkProxyRequired)
            .ShowWarning();

        private void Cancel()
        {
            CancelModelFetches();
            OnClose?.Invoke(null);
            _dialogManager.Close(this);
        }

        private Task FetchModelsManuallyAsync()
        {
            CancelScheduledModelFetch();
            return FetchModelsAsync(showErrors: true);
        }

        private async Task FetchModelsAsync(
            bool showErrors,
            CancellationToken cancellationToken = default)
        {
            _activeModelFetch?.Cancel();
            var fetch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeModelFetch = fetch;
            if (showErrors)
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
                var models = await _catalog.FetchModelsAsync(
                    new AiModelCatalogRequest(ApiUrl, ApiKey, provider),
                    fetch.Token);
                fetch.Token.ThrowIfCancellationRequested();
                AvailableModels.Clear();
                foreach (var availableModel in models)
                    AvailableModels.Add(availableModel);
                if (models.Count == 0 && showErrors)
                    FetchModelsError = Resources.NoModelsFound;
                else if (models.Count > 0 && string.IsNullOrWhiteSpace(Model))
                    Model = models[0];
                ResetModelConfirmation();
            }
            catch (OperationCanceledException) when (fetch.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (showErrors)
                    FetchModelsError = string.Format(Resources.FetchModelsFailed, exception.Message);
            }
            finally
            {
                if (ReferenceEquals(_activeModelFetch, fetch))
                {
                    _activeModelFetch = null;
                    IsFetchingModels = false;
                }
                fetch.Dispose();
            }
        }

        private void OnCatalogCredentialsChanged(bool scheduleFetch)
        {
            AvailableModels.Clear();
            FetchModelsError = string.Empty;
            ResetModelConfirmation();
            _activeModelFetch?.Cancel();
            CancelScheduledModelFetch();
            if (!scheduleFetch || string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(ApiUrl))
                return;

            var cancellation = new CancellationTokenSource();
            _scheduledModelFetch = cancellation;
            _ = FetchModelsAfterDelayAsync(cancellation);
        }

        private async Task FetchModelsAfterDelayAsync(CancellationTokenSource cancellation)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(600), cancellation.Token);
                await FetchModelsAsync(showErrors: false, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                if (ReferenceEquals(_scheduledModelFetch, cancellation))
                    _scheduledModelFetch = null;
                cancellation.Dispose();
            }
        }

        private void ResetModelConfirmation()
        {
            IsModelConfirmationRequired = false;
            ModelConfirmationMessage = string.Empty;
        }

        private void CancelScheduledModelFetch()
        {
            _scheduledModelFetch?.Cancel();
            _scheduledModelFetch = null;
        }

        private void CancelModelFetches()
        {
            CancelScheduledModelFetch();
            _activeModelFetch?.Cancel();
            _activeModelFetch = null;
            IsFetchingModels = false;
        }

        private void UpdateDefaults(AiModelType type)
        {
            (ApiUrl, Model, Name) = type switch
            {
                AiModelType.OpenAi => ("https://api.openai.com/v1", string.Empty, "OpenAI"),
                AiModelType.Gemini => ("https://generativelanguage.googleapis.com/v1beta/openai/", string.Empty, "Gemini"),
                AiModelType.Claude => ("https://api.anthropic.com/v1/", string.Empty, "Claude"),
                AiModelType.DeepSeek => ("https://api.deepseek.com/v1", string.Empty, "DeepSeek"),
                AiModelType.Qwen => ("https://dashscope.aliyuncs.com/compatible-mode/v1", string.Empty, "通义千问"),
                AiModelType.Zhipu => ("https://open.bigmodel.cn/api/paas/v4/", string.Empty, "智谱 AI"),
                AiModelType.Moonshot => ("https://api.moonshot.cn/v1", string.Empty, "月之暗面 Kimi"),
                AiModelType.Doubao => ("https://ark.cn-beijing.volces.com/api/v3", string.Empty, "字节跳动豆包"),
                AiModelType.MiniMax => ("https://api.minimaxi.com/v1", string.Empty, "MiniMax"),
                AiModelType.Hunyuan => ("https://api.hunyuan.cloud.tencent.com/v1", string.Empty, "腾讯混元"),
                AiModelType.Grok => ("https://api.x.ai/v1", string.Empty, "Grok"),
                AiModelType.Mistral => ("https://api.mistral.ai/v1", string.Empty, "Mistral AI"),
                AiModelType.Qianfan => ("https://qianfan.baidubce.com/v2", string.Empty, "百度千帆"),
                AiModelType.Spark => ("https://spark-api-open.xf-yun.com/v1", string.Empty, "讯飞星火"),
                AiModelType.StepFun => ("https://api.stepfun.com/v1", string.Empty, "阶跃星辰"),
                AiModelType.ModelScope => ("https://api-inference.modelscope.cn/v1", string.Empty, "魔搭 ModelScope"),
                AiModelType.SiliconFlow => ("https://api.siliconflow.cn/v1", string.Empty, "硅基流动"),
                AiModelType.XiaomiMimo => ("https://api.xiaomimimo.com/v1", string.Empty, "小米 MiMo"),
                AiModelType.OpenRouter => ("https://openrouter.ai/api/v1", string.Empty, "OpenRouter"),
                AiModelType.Together => ("https://api.together.xyz/v1", string.Empty, "Together AI"),
                AiModelType.Fireworks => ("https://api.fireworks.ai/inference/v1", string.Empty, "Fireworks AI"),
                AiModelType.Groq => ("https://api.groq.com/openai/v1", string.Empty, "Groq"),
                AiModelType.Cerebras => ("https://api.cerebras.ai/v1", string.Empty, "Cerebras"),
                AiModelType.DeepInfra => ("https://api.deepinfra.com/v1/openai", string.Empty, "DeepInfra"),
                AiModelType.NvidiaNim => ("https://integrate.api.nvidia.com/v1", string.Empty, "NVIDIA NIM"),
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
        private readonly DialogManager _dialogManager;
        private readonly KeyListType _type;

        public KeyListEditorViewModel(
            DialogManager dialogManager,
            string title,
            KeyListType type,
            IEnumerable<KeyItemViewModelBase> items)
        {
            _dialogManager = dialogManager;
            _type = type;
            Title = title;
            Items = new ObservableCollection<KeyItemViewModelBase>(items);
            AddCommand = ReactiveCommand.Create(Add);
            RemoveCommand = ReactiveCommand.Create<KeyItemViewModelBase>(item => Items.Remove(item));
            SaveCommand = ReactiveCommand.Create(Save);
            CancelCommand = ReactiveCommand.Create(() => dialogManager.Close(this));
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
            _dialogManager.Close(this);
        }
    }
}
