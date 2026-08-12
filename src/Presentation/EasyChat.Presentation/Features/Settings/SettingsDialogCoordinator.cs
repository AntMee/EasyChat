using EasyChat.Contracts.AiModels;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;
using EasyChat.Presentation.Lang;
using EasyChat.Presentation.Features.Capture;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Settings.Translation;
using EasyChat.Presentation.Features.Speech;
using ShadUI;

namespace EasyChat.Presentation.Features.Settings;

public sealed class SettingsDialogCoordinator(
    DialogManager dialogs,
    ToastManager toasts,
    SettingsSession settings,
    IAiModelCatalogTransport modelCatalog,
    ITtsUseCases tts,
    IScreenRegionPicker regionPicker,
    IRunningProcessCatalog runningProcesses) : ISettingsDialogCoordinator
{
    private readonly DialogManager _dialogs = dialogs;
    private readonly ToastManager _toasts = toasts;
    private readonly SettingsSession _settings = settings;
    private readonly IAiModelCatalogTransport _modelCatalog = modelCatalog;
    private readonly ITtsUseCases _tts = tts;
    private readonly IScreenRegionPicker _regionPicker = regionPicker;
    private readonly IRunningProcessCatalog _runningProcesses = runningProcesses;

    public void EditAiModel(CustomAiModelState? model)
    {
        var viewModel = new AiModelEditDialogViewModel(_dialogs, _modelCatalog, _settings, _toasts, model)
        {
            OnClose = result => SaveModel(model, result)
        };
        _dialogs.CreateDialog(viewModel).Show();
    }

    public void DeleteAiModel(CustomAiModelState model) =>
        _dialogs.CreateDialog(Resources.ConfirmDeletion, Resources.ConfirmDeleteModel)
            .WithPrimaryButton(Resources.Delete, (Action)(() => _settings.AiModel.ConfiguredModels.Remove(model)))
            .WithSecondaryButton(Resources.Cancel, (Action)(() => { }))
            .Show();

    public void ConfirmDeleteAsrModel(SpeechRecognitionModel model, Action onConfirmed) =>
        _dialogs.CreateDialog(Resources.ConfirmDeletion, string.Format(Resources.ConfirmDeleteAsrModel, model.Id))
            .WithPrimaryButton(Resources.Delete, onConfirmed)
            .WithSecondaryButton(Resources.Cancel, (Action)(() => { }))
            .Show();

    public void EditAiModelKeys(CustomAiModelState model) => ShowStringKeys(
        $"{model.Name} API Keys",
        model.ApiKeys,
        values => Replace(model.ApiKeys, values));

    public void EditBaiduKeys()
    {
        var items = _settings.MachineTranslation.Baidu.Items.Select(item =>
            (KeyItemViewModelBase)new BaiduKeyItemViewModel
            {
                AppId = item.AppId,
                AppKey = item.AppKey
            });
        ShowKeyEditor(Resources.Baidu, KeyListType.Baidu, items, edited =>
        {
            _settings.MachineTranslation.Baidu.Items.Clear();
            foreach (var item in edited.OfType<BaiduKeyItemViewModel>())
            {
                if (!string.IsNullOrWhiteSpace(item.AppId) || !string.IsNullOrWhiteSpace(item.AppKey))
                {
                    _settings.MachineTranslation.Baidu.Items.Add(new BaiduCredentialState(
                        new BaiduCredentialSettings(item.AppId, item.AppKey),
                        _settings.FlushSection));
                }
            }
        });
    }

    public void EditTencentKeys()
    {
        var items = _settings.MachineTranslation.Tencent.Items.Select(item =>
            (KeyItemViewModelBase)new TencentKeyItemViewModel
            {
                SecretId = item.SecretId,
                SecretKey = item.SecretKey
            });
        ShowKeyEditor(Resources.Tencent, KeyListType.Tencent, items, edited =>
        {
            _settings.MachineTranslation.Tencent.Items.Clear();
            foreach (var item in edited.OfType<TencentKeyItemViewModel>())
            {
                if (!string.IsNullOrWhiteSpace(item.SecretId) || !string.IsNullOrWhiteSpace(item.SecretKey))
                {
                    _settings.MachineTranslation.Tencent.Items.Add(new TencentCredentialState(
                        new TencentCredentialSettings(item.SecretId, item.SecretKey),
                        _settings.FlushSection));
                }
            }
        });
    }

    public void EditGoogleKeys() => ShowStringKeys(
        Resources.Google,
        _settings.MachineTranslation.Google.ApiKeys,
        values => Replace(_settings.MachineTranslation.Google.ApiKeys, values));

    public void EditDeepLKeys() => ShowStringKeys(
        Resources.DeepL,
        _settings.MachineTranslation.DeepL.ApiKeys,
        values => Replace(_settings.MachineTranslation.DeepL.ApiKeys, values));

    public void ManageFixedAreas()
    {
        var viewModel = new FixedAreaEditDialogViewModel(_dialogs, _settings, _regionPicker);
        _dialogs.CreateDialog(viewModel).Show();
    }

    public void ConfigureTts()
    {
        var viewModel = new TtsVoiceSettingsDialogViewModel(
            _dialogs, _toasts, _tts, _settings.Tts);
        _dialogs.CreateDialog(viewModel).Show();
    }

    public void ShowInformation(string title, string message) =>
        _dialogs.CreateDialog(title, message)
            .WithPrimaryButton(Resources.Confirm, (Action)(() => { }))
            .Show();

    public void ManageSelectionApps()
    {
        var viewModel = new SelectionAppListDialogViewModel(_dialogs, _settings, _runningProcesses);
        _dialogs.CreateDialog(viewModel).Show();
    }

    private void SaveModel(CustomAiModelState? existing, CustomAiModelSettings? value)
    {
        if (value is null)
            return;
        if (existing is null)
        {
            _settings.AiModel.ConfiguredModels.Add(new CustomAiModelState(value, _settings.FlushSection));
            return;
        }

        existing.Name = value.Name;
        existing.ModelType = value.ModelType;
        existing.ApiUrl = value.ApiUrl;
        existing.Model = value.Model;
        existing.UseProxy = value.UseProxy;
        existing.EnableThinking = value.EnableThinking;
        Replace(existing.ApiKeys, value.ApiKeys);
    }

    private void ShowStringKeys(
        string title,
        IEnumerable<string> values,
        Action<IReadOnlyList<string>> save)
    {
        var items = values.Select(value =>
            (KeyItemViewModelBase)new StringKeyItemViewModel { Value = value });
        ShowKeyEditor(title, KeyListType.String, items, edited => save(
            edited.OfType<StringKeyItemViewModel>()
                .Select(item => item.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray()));
    }

    private void ShowKeyEditor(
        string title,
        KeyListType type,
        IEnumerable<KeyItemViewModelBase> items,
        Action<IReadOnlyList<KeyItemViewModelBase>> save)
    {
        var viewModel = new KeyListEditorViewModel(_dialogs, title, type, items)
        {
            OnSave = save
        };
        _dialogs.CreateDialog(viewModel).Show();
    }

    private static void Replace<T>(ICollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }
}
