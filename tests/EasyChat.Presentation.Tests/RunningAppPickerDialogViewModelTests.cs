using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Features.Settings;
using EasyChat.Shared.Results;
using ShadUI;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class RunningAppPickerDialogViewModelTests
{
    [TestMethod]
    public async Task ConfirmCommand_StaysDisabledUntilAnAppIsSelected()
    {
        var viewModel = new RunningAppPickerDialogViewModel(
            new DialogManager(),
            new FakeRunningProcessCatalog(
                new RunningProcessDescriptor("app.exe", "app", "App Description", "App Window", ReadOnlyMemory<byte>.Empty)));

        await WaitUntilAsync(() => viewModel.FilteredApps.Count > 0);

        Assert.IsFalse(await viewModel.ConfirmCommand.CanExecute.FirstAsync());

        viewModel.FilteredApps[0].IsSelected = true;

        Assert.IsTrue(await viewModel.ConfirmCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    public async Task ConfirmCommand_DisabledAgainWhenSelectedAppIsFilteredOut()
    {
        var viewModel = new RunningAppPickerDialogViewModel(
            new DialogManager(),
            new FakeRunningProcessCatalog(
                new RunningProcessDescriptor("app.exe", "app", "App Description", "App Window", ReadOnlyMemory<byte>.Empty),
                new RunningProcessDescriptor("editor.exe", "editor", null, "Editor Window", ReadOnlyMemory<byte>.Empty)));

        await WaitUntilAsync(() => viewModel.FilteredApps.Count == 2);

        viewModel.FilteredApps[0].IsSelected = true;
        Assert.IsTrue(await viewModel.ConfirmCommand.CanExecute.FirstAsync());

        viewModel.SearchText = "editor";

        Assert.IsFalse(await viewModel.ConfirmCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    public async Task ToggleCommand_SelectsTheRowAndEnablesConfirm()
    {
        var viewModel = new RunningAppPickerDialogViewModel(
            new DialogManager(),
            new FakeRunningProcessCatalog(
                new RunningProcessDescriptor("app.exe", "app", "App Description", "App Window", ReadOnlyMemory<byte>.Empty)));

        await WaitUntilAsync(() => viewModel.FilteredApps.Count > 0);
        var row = viewModel.FilteredApps[0];
        Assert.IsFalse(row.IsSelected);

        await viewModel.ToggleAppSelectionCommand.Execute(row).ToTask();

        Assert.IsTrue(row.IsSelected);
        Assert.IsTrue(await viewModel.ConfirmCommand.CanExecute.FirstAsync());

        await viewModel.ToggleAppSelectionCommand.Execute(row).ToTask();

        Assert.IsFalse(row.IsSelected);
        Assert.IsFalse(await viewModel.ConfirmCommand.CanExecute.FirstAsync());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            if (timeout.IsCancellationRequested)
                throw new TimeoutException("Timed out waiting for the running app picker to load.");
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeRunningProcessCatalog(params RunningProcessDescriptor[] apps) : IRunningProcessCatalog
    {
        public ValueTask<IReadOnlyList<RunningProcessDescriptor>> GetRunningProcessesAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<RunningProcessDescriptor>>(apps);

        public ValueTask<Result<string>> ResolveProcessIdentifierAsync(
            ExternalTargetToken target,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<string>.Success(target.Value));
    }
}
