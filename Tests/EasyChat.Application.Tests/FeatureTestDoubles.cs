using System.Runtime.CompilerServices;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Tests;

internal sealed class AvailablePlatformAccess : IPlatformAccessUseCases
{
    public ValueTask<Result<CapabilityStatus>> EnsureAvailableAsync(
        PlatformCapability capability,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Result<CapabilityStatus>.Success(new CapabilityStatus(
            capability,
            CapabilityState.Available)));

    public ValueTask<Result<PermissionStatus>> EnsurePermissionAsync(
        PlatformPermission permission,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Result<PermissionStatus>.Success(new PermissionStatus(
            permission,
            PermissionState.Granted)));
}

internal sealed class MutableSettingsUseCases(SettingsBundle current) : ISettingsUseCases
{
    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    public event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed
    {
        add { }
        remove { }
    }

    public bool IsInitialized => true;
    public SettingsBundle Current { get; private set; } = current;

    public ValueTask<Result<SettingsBundle>> InitializeAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Result<SettingsBundle>.Success(Current));

    public Result Update(SettingsSection section, SettingsBundle settings)
    {
        Current = settings;
        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(section, settings));
        return Result.Success();
    }

    public ValueTask<Result> FlushAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Result.Success());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingTranslationProviderFactory : ITranslationProviderFactory
{
    public RecordingChatProvider Chat { get; } = new();
    public RecordingMachineProvider Machine { get; } = new();
    public AiTranslationProviderOptions? AiOptions { get; private set; }
    public MachineTranslationProviderOptions? MachineOptions { get; private set; }

    public IChatTranslationProvider Create(AiTranslationProviderOptions options)
    {
        AiOptions = options;
        return Chat;
    }

    public ITranslationProvider Create(MachineTranslationProviderOptions options)
    {
        MachineOptions = options;
        return Machine;
    }
}

internal sealed class RecordingChatProvider : IChatTranslationProvider
{
    public IReadOnlyList<string> StreamChunks { get; set; } = [];
    public string CompleteResponse { get; set; } = string.Empty;
    public ChatTranslationProviderRequest? LastRequest { get; private set; }

    public Task<string> CompleteAsync(
        ChatTranslationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(CompleteResponse);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ChatTranslationProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        foreach (var chunk in StreamChunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
        }
        await Task.CompletedTask;
    }
}

internal sealed class RecordingMachineProvider : ITranslationProvider
{
    public string Response { get; set; } = string.Empty;
    public TranslationProviderRequest? LastRequest { get; private set; }

    public Task<string> TranslateAsync(
        TranslationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(Response);
    }
}

internal sealed class RecordingTranslationFailureSink : ITranslationFailureSink
{
    public Exception? Exception { get; private set; }
    public void Report(Exception exception) => Exception = exception;
}
