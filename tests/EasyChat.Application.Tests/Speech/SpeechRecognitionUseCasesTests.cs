using System.Runtime.CompilerServices;
using EasyChat.Application.Speech;
using EasyChat.Application.Tests.Settings;
using EasyChat.Application.Translation;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;
using EasyChat.Contracts.Translation;
using EasyChat.Shared.Results;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyChat.Application.Tests.Speech;

[TestClass]
public sealed class SpeechRecognitionUseCasesTests
{
    [TestMethod]
    public async Task RecognitionEventsBecomeFrameworkNeutralSubtitleUpdates()
    {
        var initial = SettingsTestData.CreateBundle();
        var settings = new MutableSettingsUseCases(initial with
        {
            SpeechRecognition = initial.SpeechRecognition with
            {
                RecognitionLanguage = "en",
                IsTranslationEnabled = false
            }
        });
        var useCases = new SpeechRecognitionUseCases(
            new FakeEngine(),
            new AvailablePlatformAccess(),
            settings,
            new UnusedTranslationUseCases(),
            new BuiltInTranslationLanguageCatalog(),
            NullLogger<SpeechRecognitionUseCases>.Instance);

        var events = new List<SpeechSessionEvent>();
        await foreach (var item in useCases.RecognizeAsync(
                           new SpeechRecognitionCommand(
                               "en",
                               "en",
                               [new AudioCaptureSourceReference(
                                   new AudioCaptureSourceToken("test:system-output"),
                                   AudioCaptureSourceKind.SystemOutput)])))
        {
            events.Add(item);
        }

        Assert.IsInstanceOfType<SpeechSessionStartedEvent>(events[0]);
        Assert.IsTrue(events.OfType<SpeechSubtitleChangedEvent>()
            .Any(item => item.Subtitle.OriginalText == "Hello."));
        Assert.IsInstanceOfType<SpeechSessionStoppedEvent>(events[^1]);
    }

    [TestMethod]
    public async Task SubtitleIdsRemainUniqueAcrossRecognitionSessions()
    {
        var initial = SettingsTestData.CreateBundle();
        var settings = new MutableSettingsUseCases(initial with
        {
            SpeechRecognition = initial.SpeechRecognition with
            {
                RecognitionLanguage = "en",
                IsTranslationEnabled = false,
                AutoClearInterval = 0,
                FloatingDisplayMode = FloatingDisplayMode.Segmented,
                MaxFloatingHistory = 1
            }
        });
        var useCases = new SpeechRecognitionUseCases(
            new SequentialEngine(),
            new AvailablePlatformAccess(),
            settings,
            new UnusedTranslationUseCases(),
            new BuiltInTranslationLanguageCatalog(),
            NullLogger<SpeechRecognitionUseCases>.Instance);
        var command = new SpeechRecognitionCommand(
            "en",
            "en",
            [new AudioCaptureSourceReference(
                new AudioCaptureSourceToken("test:system-output"),
                AudioCaptureSourceKind.SystemOutput)]);

        var first = await useCases.RecognizeAsync(command).ToListAsync();
        var second = await useCases.RecognizeAsync(command).ToListAsync();
        var firstId = first.OfType<SpeechSubtitleChangedEvent>().Last().Subtitle.Id;
        var secondId = second.OfType<SpeechSubtitleChangedEvent>().Last().Subtitle.Id;

        Assert.IsGreaterThan(firstId, secondId);
        Assert.IsTrue(second.OfType<SpeechFloatingSubtitleRemovedEvent>()
            .Any(item => item.SubtitleId == firstId));
        Assert.IsFalse(second.OfType<SpeechFloatingSubtitleRemovedEvent>()
            .Any(item => item.SubtitleId == secondId));
    }

    [TestMethod]
    public async Task SingleUtteranceModeMergesRecognitionFragmentsIntoOneSubtitle()
    {
        var initial = SettingsTestData.CreateBundle();
        var settings = new MutableSettingsUseCases(initial with
        {
            SpeechRecognition = initial.SpeechRecognition with
            {
                RecognitionLanguage = "en",
                IsTranslationEnabled = false,
                AutoClearInterval = 0
            }
        });
        var useCases = new SpeechRecognitionUseCases(
            new MultiFragmentEngine(),
            new AvailablePlatformAccess(),
            settings,
            new UnusedTranslationUseCases(),
            new BuiltInTranslationLanguageCatalog(),
            NullLogger<SpeechRecognitionUseCases>.Instance);

        var events = await useCases.RecognizeAsync(new SpeechRecognitionCommand(
            "en",
            "en",
            [new AudioCaptureSourceReference(
                new AudioCaptureSourceToken("test:microphone"),
                AudioCaptureSourceKind.Microphone)],
            SegmentationMode: SpeechRecognitionSegmentationMode.SingleUtterance)).ToListAsync();

        var subtitles = events.OfType<SpeechSubtitleChangedEvent>()
            .GroupBy(item => item.Subtitle.Id)
            .Select(group => group.Last().Subtitle)
            .Where(subtitle => !string.IsNullOrWhiteSpace(subtitle.OriginalText))
            .ToArray();
        Assert.HasCount(1, subtitles);
        Assert.AreEqual("First sentence. Second sentence.", subtitles[0].OriginalText);
    }

    [TestMethod]
    public async Task PreparationChecksMicrophonePermissionAndForwardsTheSessionSources()
    {
        var initial = SettingsTestData.CreateBundle();
        var settings = new MutableSettingsUseCases(initial);
        var engine = new PreparationRecordingEngine();
        var access = new RecordingPlatformAccess();
        var useCases = new SpeechRecognitionUseCases(
            engine,
            access,
            settings,
            new UnusedTranslationUseCases(),
            new BuiltInTranslationLanguageCatalog(),
            NullLogger<SpeechRecognitionUseCases>.Instance);
        var microphone = new AudioCaptureSourceReference(
            new AudioCaptureSourceToken("test:microphone"),
            AudioCaptureSourceKind.Microphone);
        var command = new SpeechRecognitionCommand("en", "en", [microphone]);

        var prepared = await useCases.PrepareAsync(command);
        var released = await useCases.ReleasePreparationAsync(command);

        Assert.IsTrue(prepared.IsSuccess);
        Assert.IsTrue(released.IsSuccess);
        CollectionAssert.AreEqual(
            new[] { PlatformCapability.SpeechRecognition },
            access.Capabilities);
        CollectionAssert.AreEqual(
            new[] { PlatformPermission.Microphone },
            access.Permissions);
        Assert.IsNotNull(engine.Prepared);
        Assert.IsNotNull(engine.Released);
        CollectionAssert.AreEqual(
            new[] { microphone.Token },
            engine.Prepared.Sources.ToArray());
        CollectionAssert.AreEqual(
            new[] { microphone.Token },
            engine.Released.Sources.ToArray());
    }

    [TestMethod]
    public async Task SubtitleTimestampsRemainOrderedAcrossMidnight()
    {
        var initial = SettingsTestData.CreateBundle();
        var settings = new MutableSettingsUseCases(initial with
        {
            SpeechRecognition = initial.SpeechRecognition with
            {
                RecognitionLanguage = "en",
                IsTranslationEnabled = false,
                AutoClearInterval = 0
            }
        });
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 1, 1, 23, 59, 59, TimeSpan.Zero));
        var useCases = new SpeechRecognitionUseCases(
            new SequentialEngine(),
            new AvailablePlatformAccess(),
            settings,
            new UnusedTranslationUseCases(),
            new BuiltInTranslationLanguageCatalog(),
            NullLogger<SpeechRecognitionUseCases>.Instance,
            time);
        var command = new SpeechRecognitionCommand("en", "en", []);

        var first = await useCases.RecognizeAsync(command).ToListAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        var second = await useCases.RecognizeAsync(command).ToListAsync();
        var firstTimestamp = first.OfType<SpeechSubtitleChangedEvent>()
            .Last().Subtitle.Timestamp;
        var secondTimestamp = second.OfType<SpeechSubtitleChangedEvent>()
            .Last().Subtitle.Timestamp;

        Assert.IsGreaterThan(firstTimestamp, secondTimestamp);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromDays(1), secondTimestamp);
    }

    private sealed class FakeEngine : ISpeechRecognitionEngine
    {
        public async IAsyncEnumerable<SpeechRecognitionEvent> RecognizeAsync(
            SpeechRecognitionOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Started);
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Partial, "Hello");
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Final, "Hello.");
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Stopped);
            await Task.CompletedTask;
        }
    }

    private sealed class SequentialEngine : ISpeechRecognitionEngine
    {
        private int _session;

        public async IAsyncEnumerable<SpeechRecognitionEvent> RecognizeAsync(
            SpeechRecognitionOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var session = Interlocked.Increment(ref _session);
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Started);
            yield return new SpeechRecognitionEvent(
                SpeechRecognitionEventKind.Final,
                $"Session {session}.");
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Stopped);
            await Task.CompletedTask;
        }
    }

    private sealed class MultiFragmentEngine : ISpeechRecognitionEngine
    {
        public async IAsyncEnumerable<SpeechRecognitionEvent> RecognizeAsync(
            SpeechRecognitionOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Started);
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Partial, "First sentence");
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Final, "First sentence.");
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Partial, "Second sentence");
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Final, "Second sentence.");
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Stopped);
            await Task.CompletedTask;
        }
    }

    private sealed class PreparationRecordingEngine : ISpeechRecognitionEngine
    {
        public SpeechRecognitionOptions? Prepared { get; private set; }
        public SpeechRecognitionOptions? Released { get; private set; }

        public ValueTask PrepareAsync(
            SpeechRecognitionOptions options,
            CancellationToken cancellationToken = default)
        {
            Prepared = options;
            return ValueTask.CompletedTask;
        }

        public ValueTask ReleasePreparationAsync(
            SpeechRecognitionOptions options,
            CancellationToken cancellationToken = default)
        {
            Released = options;
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<SpeechRecognitionEvent> RecognizeAsync(
            SpeechRecognitionOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Stopped);
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingPlatformAccess : IPlatformAccessUseCases
    {
        public List<PlatformCapability> Capabilities { get; } = [];
        public List<PlatformPermission> Permissions { get; } = [];

        public ValueTask<Result<CapabilityStatus>> EnsureAvailableAsync(
            PlatformCapability capability,
            CancellationToken cancellationToken = default)
        {
            Capabilities.Add(capability);
            return ValueTask.FromResult(Result<CapabilityStatus>.Success(
                new CapabilityStatus(capability, CapabilityState.Available)));
        }

        public ValueTask<Result<PermissionStatus>> EnsurePermissionAsync(
            PlatformPermission permission,
            CancellationToken cancellationToken = default)
        {
            Permissions.Add(permission);
            return ValueTask.FromResult(Result<PermissionStatus>.Success(
                new PermissionStatus(permission, PermissionState.Granted)));
        }
    }

    private sealed class UnusedTranslationUseCases : ITranslationUseCases
    {
        public ITranslationSession Prepare(TranslationProviderSelection? provider = null) =>
            throw new AssertFailedException("Translation must remain disabled in this test.");

        public Task<Result<TranslationResponse>> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Translation must remain disabled in this test.");

        public IAsyncEnumerable<TranslationEvent> StreamAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Translation must remain disabled in this test.");
    }
}
