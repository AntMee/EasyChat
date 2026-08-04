using EasyChat.Contracts.Translation;

namespace EasyChat.Application.Speech;

internal sealed class SubtitleTranslationLane
{
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<object, ProviderRunState> _providerRuns =
        new(ReferenceEqualityComparer.Instance);
    private object? _holderRunKey;

    public async Task WaitAsync(object runKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runKey);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
            _holderRunKey = runKey;
    }

    public void RegisterProviderRun(
        object runKey,
        TranslationProviderSelection selection)
    {
        ArgumentNullException.ThrowIfNull(runKey);
        ArgumentNullException.ThrowIfNull(selection);
        lock (_sync)
            _providerRuns.Add(runKey, new ProviderRunState(selection));
    }

    public void MarkTimedOut(object runKey)
    {
        ArgumentNullException.ThrowIfNull(runKey);
        lock (_sync)
        {
            if (_providerRuns.TryGetValue(runKey, out var run))
                run.TimedOut = true;
            if (_holderRunKey is not null
                && _providerRuns.TryGetValue(_holderRunKey, out var holder))
            {
                holder.TimedOut = true;
            }
        }
    }

    public void CompleteProviderRun(object runKey, bool gateHeld)
    {
        ArgumentNullException.ThrowIfNull(runKey);
        lock (_sync)
        {
            _providerRuns.Remove(runKey);
            if (ReferenceEquals(_holderRunKey, runKey))
                _holderRunKey = null;
        }
        if (gateHeld)
            _gate.Release();
    }

    public bool IsUnavailable()
    {
        lock (_sync)
            return _providerRuns.Values.Any(run => run.TimedOut);
    }

    private sealed class ProviderRunState(TranslationProviderSelection selection)
    {
        public TranslationProviderSelection Selection { get; } = selection;
        public bool TimedOut { get; set; }
    }
}
