using EasyChat.Contracts.Settings;

namespace EasyChat.Application.Speech;

internal sealed class SubtitleFloatingLifecycleRegistry
{
    private readonly Lock _sync = new();
    private readonly Dictionary<long, FloatingEntry> _entries = [];
    private readonly TimeProvider _timeProvider;
    private readonly long _startTimestamp;

    public SubtitleFloatingLifecycleRegistry(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _startTimestamp = _timeProvider.GetTimestamp();
    }

    public TimeSpan GetMonotonicNow() =>
        _timeProvider.GetElapsedTime(_startTimestamp, _timeProvider.GetTimestamp());

    public IReadOnlyList<long> Update(
        long subtitleId,
        bool isSealed,
        bool isTerminal,
        TimeSpan? expiresAt,
        TimeSpan now,
        FloatingDisplayMode displayMode,
        int maximumHistory)
    {
        lock (_sync)
        {
            var entry = GetOrAdd(subtitleId, isVisible: true);
            entry.IsSealed = isSealed;
            entry.IsTerminal = isTerminal;
            entry.ExpiresAt = expiresAt;
            return SweepLocked(now, displayMode, maximumHistory);
        }
    }

    public IReadOnlyList<long> Materialize(
        long anchorId,
        IReadOnlyList<long> childIds,
        TimeSpan? expiresAt,
        TimeSpan now,
        FloatingDisplayMode displayMode,
        int maximumHistory)
    {
        ArgumentNullException.ThrowIfNull(childIds);
        lock (_sync)
        {
            var removed = new List<long>();
            var anchor = GetOrAdd(anchorId, isVisible: true);
            var inheritedVisibility = anchor.IsVisible;
            anchor.IsSealed = true;
            anchor.IsTerminal = true;
            anchor.ExpiresAt = expiresAt;

            foreach (var childId in childIds)
            {
                if (_entries.TryGetValue(childId, out var existing))
                {
                    existing.IsSealed = true;
                    existing.IsTerminal = true;
                    existing.ExpiresAt = expiresAt;
                    continue;
                }

                var child = GetOrAdd(childId, inheritedVisibility);
                child.IsSealed = true;
                child.IsTerminal = true;
                child.ExpiresAt = expiresAt;
                if (!inheritedVisibility)
                    PublishRemovalLocked(child, removed);
            }

            removed.AddRange(SweepLocked(now, displayMode, maximumHistory));
            return removed;
        }
    }

    public IReadOnlyList<long> Sweep(
        TimeSpan now,
        FloatingDisplayMode displayMode,
        int maximumHistory)
    {
        lock (_sync)
            return SweepLocked(now, displayMode, maximumHistory);
    }

    public IReadOnlyList<long> Remove(long subtitleId)
    {
        lock (_sync)
        {
            var removed = new List<long>(1);
            var entry = GetOrAdd(subtitleId, isVisible: false);
            entry.IsVisible = false;
            PublishRemovalLocked(entry, removed);
            return removed;
        }
    }

    public bool IsVisible(long subtitleId)
    {
        lock (_sync)
            return _entries.TryGetValue(subtitleId, out var entry) && entry.IsVisible;
    }

    public bool HasPendingExpiry()
    {
        lock (_sync)
            return _entries.Values.Any(entry => entry.IsVisible && entry.ExpiresAt is not null);
    }

    public IReadOnlyList<long> GetRemovalTombstones()
    {
        lock (_sync)
        {
            return _entries.Values
                .Where(entry => !entry.IsVisible)
                .OrderBy(entry => entry.SubtitleId)
                .Select(entry => entry.SubtitleId)
                .ToArray();
        }
    }

    private IReadOnlyList<long> SweepLocked(
        TimeSpan now,
        FloatingDisplayMode displayMode,
        int maximumHistory)
    {
        var removed = new List<long>();
        foreach (var entry in _entries.Values
                     .Where(entry => entry.IsVisible
                                     && entry.ExpiresAt is not null
                                     && entry.ExpiresAt <= now)
                     .OrderBy(entry => entry.SubtitleId)
                     .ToArray())
        {
            entry.IsVisible = false;
            PublishRemovalLocked(entry, removed);
        }

        var limit = displayMode == FloatingDisplayMode.Segmented
            ? Math.Max(1, maximumHistory)
            : 100;
        var completed = _entries.Values
            .Where(entry => entry.IsVisible && entry.IsSealed && entry.IsTerminal)
            .OrderBy(entry => entry.SubtitleId)
            .ToList();
        for (var index = 0; index < completed.Count - limit; index++)
        {
            completed[index].IsVisible = false;
            PublishRemovalLocked(completed[index], removed);
        }
        return removed;
    }

    private FloatingEntry GetOrAdd(long subtitleId, bool isVisible)
    {
        if (_entries.TryGetValue(subtitleId, out var entry))
            return entry;
        entry = new FloatingEntry(subtitleId, isVisible);
        _entries.Add(subtitleId, entry);
        return entry;
    }

    private static void PublishRemovalLocked(FloatingEntry entry, List<long> destination)
    {
        if (entry.RemovalPublished)
            return;
        entry.RemovalPublished = true;
        destination.Add(entry.SubtitleId);
    }

    private sealed class FloatingEntry(long subtitleId, bool isVisible)
    {
        public long SubtitleId { get; } = subtitleId;
        public bool IsVisible { get; set; } = isVisible;
        public bool IsSealed { get; set; }
        public bool IsTerminal { get; set; }
        public bool RemovalPublished { get; set; }
        public TimeSpan? ExpiresAt { get; set; }
    }
}
