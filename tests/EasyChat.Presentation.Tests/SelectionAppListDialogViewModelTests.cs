using System.Reactive.Threading.Tasks;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Features.Settings;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Shared.Results;
using ShadUI;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class SelectionAppListDialogViewModelTests
{
    [TestMethod]
    public async Task Apps_ReflectPersistedListAndRemoveWorks()
    {
        var session = CreateSession(["chrome.exe", "notepad.exe"]);
        var viewModel = new SelectionAppListDialogViewModel(
            new DialogManager(),
            session,
            new FakeRunningProcessCatalog());

        Assert.HasCount(2, viewModel.Apps);
        Assert.IsTrue(viewModel.HasApps);
        Assert.IsFalse(viewModel.HasNoApps);

        await viewModel.RemoveAppCommand.Execute(viewModel.Apps[0]).ToTask();

        Assert.HasCount(1, viewModel.Apps);
    }

    [TestMethod]
    public void EmptyList_ShowsNoAppsState()
    {
        var session = CreateSession([]);
        var viewModel = new SelectionAppListDialogViewModel(
            new DialogManager(),
            session,
            new FakeRunningProcessCatalog());

        Assert.IsTrue(viewModel.HasNoApps);
        Assert.IsFalse(viewModel.HasApps);
        Assert.IsEmpty(viewModel.Apps);
    }

    private static SettingsSession CreateSession(IReadOnlyList<string> appList)
    {
        var baseSettings = TextAssistCommandTests.CreateSettings();
        var bundle = baseSettings with
        {
            SelectionTranslation = baseSettings.SelectionTranslation with
            {
                FilterMode = SelectionFilterMode.Whitelist,
                AppList = appList.Select(identifier => new SelectionAppEntrySettings(identifier)).ToArray()
            }
        };
        var session = new SettingsSession(new TextAssistCommandTests.StubSettingsUseCases(bundle));
        Assert.IsTrue(session.AttachCurrent().IsSuccess);
        return session;
    }

    private sealed class FakeRunningProcessCatalog : IRunningProcessCatalog
    {
        public ValueTask<IReadOnlyList<RunningProcessDescriptor>> GetRunningProcessesAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<RunningProcessDescriptor>>([]);

        public ValueTask<Result<string>> ResolveProcessIdentifierAsync(
            ExternalTargetToken target,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<string>.Success(target.Value));
    }
}
