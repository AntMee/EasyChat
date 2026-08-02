using System.Runtime.CompilerServices;
using EasyChat.Application.Speech;
using EasyChat.Application.Tests.Settings;
using EasyChat.Application.Translation;
using EasyChat.Contracts.Platform;
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
            settings,
            new UnusedTranslationUseCases(),
            new BuiltInTranslationLanguageCatalog(),
            NullLogger<SpeechRecognitionUseCases>.Instance);

        var events = new List<SpeechSessionEvent>();
        await foreach (var item in useCases.RecognizeAsync(
                           new SpeechRecognitionCommand("en", "en", [0])))
        {
            events.Add(item);
        }

        Assert.IsInstanceOfType<SpeechSessionStartedEvent>(events[0]);
        Assert.IsTrue(events.OfType<SpeechSubtitleChangedEvent>()
            .Any(item => item.Subtitle.OriginalText == "Hello."));
        Assert.IsInstanceOfType<SpeechSessionStoppedEvent>(events[^1]);
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
