using EasyChat.Contracts.AiModels;
using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Settings.Translation;
using EasyChat.Presentation.Foundation.Formatting;
using EasyChat.Presentation.Foundation.UiHost;
using ShadUI;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class AiModelEditDialogViewModelTests
{
    [TestMethod]
    public void NewModel_DoesNotUseHardCodedModelDefaults()
    {
        var viewModel = CreateViewModel(new RecordingCatalog([]));

        Assert.AreEqual(string.Empty, viewModel.Model);

        foreach (var modelType in Enum.GetValues<AiModelType>())
        {
            viewModel.SelectedModelType = modelType;
            Assert.AreEqual(string.Empty, viewModel.Model, modelType.ToString());
        }
    }

    [TestMethod]
    public void KnownProviders_UseTheDocumentedOpenAiCompatibleApiBaseUrls()
    {
        var viewModel = CreateViewModel(new RecordingCatalog([]));
        var providers = new Dictionary<AiModelType, (string ApiUrl, string Name)>
        {
            [AiModelType.Google] = ("https://generativelanguage.googleapis.com/v1beta/openai/", "Google"),
            [AiModelType.Qwen] = ("https://dashscope.aliyuncs.com/compatible-mode/v1", "通义千问"),
            [AiModelType.Zhipu] = ("https://open.bigmodel.cn/api/paas/v4/", "智谱 AI"),
            [AiModelType.Moonshot] = ("https://api.moonshot.cn/v1", "月之暗面 Kimi"),
            [AiModelType.Doubao] = ("https://ark.cn-beijing.volces.com/api/v3", "字节跳动豆包"),
            [AiModelType.MiniMax] = ("https://api.minimaxi.com/v1", "MiniMax"),
            [AiModelType.Hunyuan] = ("https://api.hunyuan.cloud.tencent.com/v1", "腾讯混元"),
            [AiModelType.Qianfan] = ("https://qianfan.baidubce.com/v2", "百度千帆"),
            [AiModelType.Spark] = ("https://spark-api-open.xf-yun.com/v1", "讯飞星火"),
            [AiModelType.StepFun] = ("https://api.stepfun.com/v1", "阶跃星辰"),
            [AiModelType.ModelScope] = ("https://api-inference.modelscope.cn/v1", "魔搭 ModelScope"),
            [AiModelType.SiliconFlow] = ("https://api.siliconflow.cn/v1", "硅基流动"),
            [AiModelType.XiaomiMimo] = ("https://api.xiaomimimo.com/v1", "XiaoMi"),
            [AiModelType.OpenRouter] = ("https://openrouter.ai/api/v1", "OpenRouter"),
            [AiModelType.Together] = ("https://api.together.xyz/v1", "Together AI"),
            [AiModelType.Fireworks] = ("https://api.fireworks.ai/inference/v1", "Fireworks AI"),
            [AiModelType.Groq] = ("https://api.groq.com/openai/v1", "Groq"),
            [AiModelType.Cerebras] = ("https://api.cerebras.ai/v1", "Cerebras"),
            [AiModelType.DeepInfra] = ("https://api.deepinfra.com/v1/openai", "DeepInfra"),
            [AiModelType.NvidiaNim] = ("https://integrate.api.nvidia.com/v1", "NVIDIA NIM")
        };

        foreach (var (provider, expected) in providers)
        {
            viewModel.SelectedModelType = provider;

            Assert.AreEqual(expected.ApiUrl, viewModel.ApiUrl, provider.ToString());
            Assert.AreEqual(AiModelTypeConverters.GetDisplayName(provider), viewModel.Name, provider.ToString());
            Assert.AreEqual(string.Empty, viewModel.Model, provider.ToString());
        }
    }

    [TestMethod]
    public async Task ModelTypeChange_SelectsModelReturnedByFetch()
    {
        var existing = new CustomAiModelState(
            new CustomAiModelSettings(
                "model-id",
                "Existing",
                AiModelType.OpenAi,
                ["api-key"],
                "https://api.openai.com/v1",
                "existing-model",
                false,
                false),
            _ => EasyChat.Shared.Results.Result.Success());
        var viewModel = CreateViewModel(new RecordingCatalog(["fetched-model"]), existing: existing);

        viewModel.SelectedModelType = AiModelType.DeepSeek;
        Assert.AreEqual(string.Empty, viewModel.Model);

        ((System.Windows.Input.ICommand)viewModel.FetchModelsCommand).Execute(null);
        await WaitForAsync(() => viewModel.Model == "fetched-model");

        Assert.AreEqual("fetched-model", viewModel.Model);
    }

    [TestMethod]
    public async Task ApiKeyChange_SilentlyFetchesModelsAfterDebounce()
    {
        var catalog = new RecordingCatalog(new InvalidOperationException("Unavailable"));
        var viewModel = CreateViewModel(catalog);

        viewModel.ApiKey = "updated-key";
        await WaitForAsync(() => catalog.CallCount > 0);

        Assert.AreEqual(string.Empty, viewModel.FetchModelsError);
        Assert.IsFalse(viewModel.IsFetchingModels);
    }

    [TestMethod]
    public async Task ExistingModel_InitializationFetchesModelsAndAllowsSavingWithoutConfirmation()
    {
        CustomAiModelSettings? saved = null;
        var existing = new CustomAiModelState(
            new CustomAiModelSettings(
                "model-id",
                "Existing",
                AiModelType.OpenAi,
                ["api-key"],
                "https://api.openai.com/v1",
                "existing-model",
                false,
                false),
            _ => EasyChat.Shared.Results.Result.Success());
        var catalog = new RecordingCatalog(["existing-model", "another-model"]);
        var viewModel = CreateViewModel(catalog, result => saved = result, existing);

        await viewModel.InitializeAsync();
        await viewModel.InitializeAsync();

        Assert.AreEqual(1, catalog.CallCount);
        CollectionAssert.Contains(viewModel.AvailableModels, "existing-model");

        ((System.Windows.Input.ICommand)viewModel.SaveCommand).Execute(null);
        await WaitForAsync(() => saved is not null);

        Assert.IsFalse(viewModel.IsModelConfirmationRequired);
        Assert.AreEqual("existing-model", saved!.Model);
    }

    [TestMethod]
    public async Task UnknownModel_RequiresConfirmationBeforeSaving()
    {
        CustomAiModelSettings? saved = null;
        var viewModel = CreateViewModel(new RecordingCatalog([]), result => saved = result);
        viewModel.Model = "manually-entered-model";

        await WaitForAsync(() => ((System.Windows.Input.ICommand)viewModel.SaveCommand).CanExecute(null));
        ((System.Windows.Input.ICommand)viewModel.SaveCommand).Execute(null);
        await WaitForAsync(() => viewModel.IsModelConfirmationRequired);

        Assert.IsTrue(viewModel.IsModelConfirmationRequired);
        Assert.IsNull(saved);

        await WaitForAsync(() => ((System.Windows.Input.ICommand)viewModel.ConfirmSaveCommand).CanExecute(null));
        ((System.Windows.Input.ICommand)viewModel.ConfirmSaveCommand).Execute(null);
        await WaitForAsync(() => saved is not null);

        Assert.IsNotNull(saved);
        Assert.AreEqual(viewModel.Model, saved.Model);
    }

    private static AiModelEditDialogViewModel CreateViewModel(
        IAiModelCatalogTransport catalog,
        Action<CustomAiModelSettings?>? onClose = null,
        CustomAiModelState? existing = null)
    {
        var settings = new SettingsSession(
            new TextAssistCommandTests.StubSettingsUseCases(TextAssistCommandTests.CreateSettings()));
        Assert.IsTrue(settings.AttachCurrent().IsSuccess);
        return new(new DialogManager(), catalog, settings, new ToastManager(), existing)
        {
            OnClose = onClose
        };
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(25, timeout.Token);
    }

    private sealed class RecordingCatalog : IAiModelCatalogTransport
    {
        private readonly IReadOnlyList<string>? _models;
        private readonly Exception? _exception;
        private int _callCount;

        public RecordingCatalog(IReadOnlyList<string> models) => _models = models;

        public RecordingCatalog(Exception exception) => _exception = exception;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<IReadOnlyList<string>> FetchModelsAsync(
            AiModelCatalogRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return _exception is null
                ? Task.FromResult(_models ?? (IReadOnlyList<string>)[])
                : Task.FromException<IReadOnlyList<string>>(_exception);
        }
    }
}
