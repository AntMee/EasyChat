using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
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
public sealed class SubtitleSessionCoordinatorTests
{
    [TestMethod]
    public async Task FinalRevisionRebuildsPublishedRangesWithoutDuplicateOrMissingText()
    {
        var settings = CreateSettings(translationEnabled: false);
        await using var harness = new CoordinatorHarness(settings);
        const string partial =
            "one two three four wrong six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty";
        const string final =
            "one two three four right six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty.";

        await harness.SendAsync(SpeechRecognitionEventKind.Started);
        await harness.SendAsync(SpeechRecognitionEventKind.Partial, partial);
        await harness.SendAsync(SpeechRecognitionEventKind.Partial, partial);
        await harness.WaitForAsync(events => LatestLines(events).Count >= 2);
        var partialLineCount = LatestLines(harness.Events).Count;
        await harness.SendAsync(SpeechRecognitionEventKind.Final, final);
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var latest = LatestLines(harness.Events)
            .Where(line => !string.IsNullOrWhiteSpace(line.OriginalText))
            .OrderBy(line => line.Id)
            .ToArray();
        Assert.HasCount(partialLineCount, latest);
        Assert.AreEqual(final, string.Join(" ", latest.Select(line => line.OriginalText)));
        Assert.IsTrue(latest.All(line => !line.IsTemporary));
    }

    [TestMethod]
    public async Task DuplicateFinalDoesNotCreateAnotherHistoryLine()
    {
        await using var harness = new CoordinatorHarness(CreateSettings(translationEnabled: false));

        await harness.SendAsync(SpeechRecognitionEventKind.Started);
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Hello world.");
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Hello world.");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var latest = LatestLines(harness.Events)
            .Where(line => !string.IsNullOrWhiteSpace(line.OriginalText))
            .ToArray();
        Assert.HasCount(1, latest);
        Assert.AreEqual("Hello world.", latest[0].OriginalText);
    }

    [TestMethod]
    public async Task IdenticalFinalAfterTheDuplicateWindowStartsANewUtterance()
    {
        await using var harness = new CoordinatorHarness(CreateSettings(translationEnabled: false));

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Yes.");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Yes.");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var latest = LatestLines(harness.Events)
            .Where(line => !string.IsNullOrWhiteSpace(line.OriginalText))
            .OrderBy(line => line.Id)
            .ToArray();
        Assert.HasCount(2, latest);
        CollectionAssert.AreEqual(new[] { "Yes.", "Yes." }, latest.Select(line => line.OriginalText).ToArray());
    }

    [TestMethod]
    public async Task RepeatedDuplicateFinalsRefreshTheSuppressionWindow()
    {
        await using var harness = new CoordinatorHarness(CreateSettings(translationEnabled: false));

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Yes.");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(TimeSpan.FromMilliseconds(400));
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Yes.");
        await harness.DrainAsync();
        harness.Time.Advance(TimeSpan.FromMilliseconds(400));
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Yes.");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual("Yes.", AssertExactlyOneLatestLine(harness.Events).OriginalText);
    }

    [TestMethod]
    public async Task PunctuationOnlyFinalCompletesTheCurrentDraft()
    {
        await using var harness = new CoordinatorHarness(CreateSettings(translationEnabled: false));

        await harness.SendAsync(SpeechRecognitionEventKind.Started);
        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "Hello world");
        await harness.SendAsync(SpeechRecognitionEventKind.Final, ".");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var line = AssertExactlyOneLatestLine(harness.Events);
        Assert.AreEqual("Hello world.", line.OriginalText);
        Assert.IsFalse(line.IsTemporary);
    }

    [TestMethod]
    public async Task PunctuationOnlyFinalKeepsClosingQuotesWithTheDraft()
    {
        await using var harness = new CoordinatorHarness(CreateSettings(translationEnabled: false));

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "他说“你好");
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "。”");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual("他说“你好。”", AssertExactlyOneLatestLine(harness.Events).OriginalText);
    }

    [TestMethod]
    public async Task CumulativeFinalAfterQuietAppendsPunctuationToTheSameLine()
    {
        await using var harness = new CoordinatorHarness(CreateSettings(translationEnabled: false));
        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "Hello world");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(IncrementalSubtitleSegmenter.QuietPeriod + TimeSpan.FromMilliseconds(100));
        await harness.WaitForAsync(events => LatestLines(events)
            .Any(line => line.OriginalText == "Hello world" && !line.IsTemporary));

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Hello world.");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var line = AssertExactlyOneLatestLine(harness.Events);
        Assert.AreEqual("Hello world.", line.OriginalText);
    }

    [TestMethod]
    public async Task ResetPartialAfterQuietStartsANewUtterance()
    {
        await using var harness = new CoordinatorHarness(CreateSettings(translationEnabled: false));
        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "first thought");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(IncrementalSubtitleSegmenter.QuietPeriod + TimeSpan.FromMilliseconds(100));
        await harness.WaitForAsync(events => LatestLines(events)
            .Any(line => line.OriginalText == "first thought" && !line.IsTemporary));

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "second thought");
        await harness.WaitForAsync(events => LatestLines(events)
            .Any(line => line.OriginalText == "second thought"));
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "second thought.");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var lines = LatestLines(harness.Events)
            .Where(line => !string.IsNullOrWhiteSpace(line.OriginalText))
            .OrderBy(line => line.Id)
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { "first thought", "second thought." },
            lines.Select(line => line.OriginalText).ToArray());
    }

    [TestMethod]
    public async Task FinalWithOnlyAnAddedTerminalSuffixUpdatesPriorFinal()
    {
        await using var harness = new CoordinatorHarness(CreateSettings(translationEnabled: false));
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Hello world");
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Hello world.");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var line = AssertExactlyOneLatestLine(harness.Events);
        Assert.AreEqual("Hello world.", line.OriginalText);
    }

    [TestMethod]
    public async Task TerminalExtensionAfterTheDuplicateWindowStartsANewUtterance()
    {
        await using var harness = new CoordinatorHarness(CreateSettings(translationEnabled: false));

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Hello world");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Hello world.");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var latest = LatestLines(harness.Events)
            .Where(line => !string.IsNullOrWhiteSpace(line.OriginalText))
            .OrderBy(line => line.Id)
            .ToArray();
        Assert.HasCount(2, latest);
        CollectionAssert.AreEqual(
            new[] { "Hello world", "Hello world." },
            latest.Select(line => line.OriginalText).ToArray());
    }

    [TestMethod]
    public async Task CumulativeFinalAppendsOnlyTheMissingTerminalCluster()
    {
        await using var harness = new CoordinatorHarness(CreateSettings(translationEnabled: false));

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Really?");
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Really?!");
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "!");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual("Really?!", AssertExactlyOneLatestLine(harness.Events).OriginalText);
    }

    [TestMethod]
    public async Task FinalCaseRevisionUpdatesAPreviouslyCommittedRange()
    {
        var settings = CreateSettings(translationEnabled: false) with { MaxSentencesPerLine = 1 };
        await using var harness = new CoordinatorHarness(settings);
        const string partial = "hello world. next sentence";
        const string final = "Hello world. next sentence.";

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, partial);
        await harness.SendAsync(SpeechRecognitionEventKind.Partial, partial);
        await harness.WaitForAsync(events => LatestLines(events).Count >= 2);
        var firstId = LatestLines(harness.Events).OrderBy(line => line.Id).First().Id;
        await harness.SendAsync(SpeechRecognitionEventKind.Final, final);
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var latest = LatestLines(harness.Events)
            .Where(line => !string.IsNullOrWhiteSpace(line.OriginalText))
            .OrderBy(line => line.Id)
            .ToArray();
        Assert.AreEqual(firstId, latest[0].Id);
        Assert.AreEqual(final, string.Join(" ", latest.Select(line => line.OriginalText)));
    }

    [TestMethod]
    public async Task FinalReconcilesARevisionInsideAnAlreadyCommittedRange()
    {
        var settings = CreateSettings(translationEnabled: false) with { MaxSentencesPerLine = 1 };
        await using var harness = new CoordinatorHarness(settings);
        const string original = "I like cats. and dogs";
        const string revised = "I like bats. and dogs.";

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, original);
        await harness.SendAsync(SpeechRecognitionEventKind.Partial, original);
        await harness.WaitForAsync(events => LatestLines(events).Count >= 2);
        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "I like bats. and dogs");
        await harness.SendAsync(SpeechRecognitionEventKind.Final, revised);
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var text = string.Join(" ", LatestLines(harness.Events)
            .Where(line => !string.IsNullOrWhiteSpace(line.OriginalText))
            .OrderBy(line => line.Id)
            .Select(line => line.OriginalText));
        Assert.AreEqual(revised, text);
    }

    [TestMethod]
    public async Task StopReconcilesARevisionInsideAnAlreadyCommittedRange()
    {
        var settings = CreateSettings(translationEnabled: false) with { MaxSentencesPerLine = 1 };
        await using var harness = new CoordinatorHarness(settings);
        const string original = "I like cats. and dogs";
        const string revised = "I like bats. and dogs";

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, original);
        await harness.SendAsync(SpeechRecognitionEventKind.Partial, original);
        await harness.WaitForAsync(events => LatestLines(events).Count >= 2);
        await harness.SendAsync(SpeechRecognitionEventKind.Partial, revised);
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var text = string.Join(" ", LatestLines(harness.Events)
            .Where(line => !string.IsNullOrWhiteSpace(line.OriginalText))
            .OrderBy(line => line.Id)
            .Select(line => line.OriginalText));
        Assert.AreEqual(revised, text);
    }

    [TestMethod]
    public async Task ShortFinalRetractsObsoleteRangesInsteadOfSplittingIntoSingleCharacters()
    {
        await using var harness = new CoordinatorHarness(CreateSettings(translationEnabled: false));
        const string partial =
            "one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty";

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, partial);
        await harness.SendAsync(SpeechRecognitionEventKind.Partial, partial);
        await harness.WaitForAsync(events => LatestLines(events).Count >= 2);
        var oldIds = LatestLines(harness.Events).OrderBy(line => line.Id).Select(line => line.Id).ToArray();
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "OK.");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var remaining = LatestLines(harness.Events)
            .Where(line => !string.IsNullOrWhiteSpace(line.OriginalText))
            .ToArray();
        var retractedIds = harness.Events.OfType<SpeechFloatingSubtitleRemovedEvent>()
            .Select(item => item.SubtitleId)
            .ToHashSet();
        Assert.HasCount(1, remaining);
        Assert.AreEqual(oldIds[0], remaining[0].Id);
        Assert.AreEqual("OK.", remaining[0].OriginalText);
        Assert.IsTrue(oldIds.Skip(1).All(retractedIds.Contains));
    }

    [TestMethod]
    public async Task StopFlushesAnUnfinishedHypothesis()
    {
        await using var harness = new CoordinatorHarness(CreateSettings(translationEnabled: false));

        await harness.SendAsync(SpeechRecognitionEventKind.Started);
        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "unfinished speech without final");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var line = AssertExactlyOneLatestLine(harness.Events);
        Assert.AreEqual("unfinished speech without final", line.OriginalText);
        Assert.IsFalse(line.IsTemporary);
    }

    [TestMethod]
    public async Task MaxSentencesPerLineRemainsAnAdditionalFinalLimit()
    {
        var settings = CreateSettings(translationEnabled: false) with { MaxSentencesPerLine = 2 };
        await using var harness = new CoordinatorHarness(settings);

        await harness.SendAsync(SpeechRecognitionEventKind.Started);
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "One. Two. Three.");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var lines = LatestLines(harness.Events)
            .Where(line => !string.IsNullOrWhiteSpace(line.OriginalText))
            .OrderBy(line => line.Id)
            .ToArray();
        Assert.HasCount(2, lines);
        Assert.AreEqual("One. Two.", lines[0].OriginalText);
        Assert.AreEqual("Three.", lines[1].OriginalText);
    }

    [TestMethod]
    public async Task ActiveDraftDoesNotExpireButSealedLineExpiresAfterTerminalState()
    {
        var settings = CreateSettings(translationEnabled: false) with { AutoClearInterval = 1 };
        await using var harness = new CoordinatorHarness(settings);

        await harness.SendAsync(SpeechRecognitionEventKind.Started);
        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "active draft");
        await harness.WaitForAsync(events => events.OfType<SpeechSubtitleChangedEvent>().Any());
        for (var index = 1; index <= 3; index++)
        {
            harness.Time.Advance(TimeSpan.FromMilliseconds(600));
            var expected = $"active draft {index}";
            await harness.SendAsync(SpeechRecognitionEventKind.Partial, expected);
            await harness.WaitForAsync(events => LatestLines(events)
                .Any(line => line.OriginalText == expected));
        }
        Assert.IsFalse(harness.Events.OfType<SpeechFloatingSubtitleRemovedEvent>().Any());

        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.WaitForAsync(events => events.OfType<SpeechSessionStoppedEvent>().Any());
        Assert.IsFalse(harness.Completion.IsCompleted);
        harness.Time.Advance(TimeSpan.FromSeconds(1.1));
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.HasCount(1, harness.Events.OfType<SpeechFloatingSubtitleRemovedEvent>());
        Assert.IsTrue(LatestLines(harness.Events).Any(line => line.OriginalText == "active draft 3"));
    }

    [TestMethod]
    public async Task AutoClearZeroCompletesWithoutTimeBasedRemoval()
    {
        var settings = CreateSettings(translationEnabled: false) with { AutoClearInterval = 0 };
        await using var harness = new CoordinatorHarness(settings);

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "persistent history.");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await harness.DrainAsync();

        Assert.IsFalse(harness.Events.OfType<SpeechFloatingSubtitleRemovedEvent>().Any());
    }

    [TestMethod]
    public async Task DisabledRealtimePreviewWaitsForFinalBeforeCallingLlm()
    {
        var translations = new RecordingTranslationUseCases("最终翻译");
        var settings = CreateSettings(translationEnabled: true) with
        {
            IsRealTimePreviewEnabled = false
        };
        await using var harness = new CoordinatorHarness(settings, translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "one two three four");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(TimeSpan.FromMilliseconds(400));
        await harness.DrainAsync();
        Assert.AreEqual(0, translations.RequestCount);

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "one two three four.");
        await harness.WaitForAsync(_ => translations.RequestCount >= 1);
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("最终翻译", LatestLines(harness.Events).Single().DisplayTranslatedText);
    }

    [TestMethod]
    public async Task SlowLlmPreviewUsesDebounceShadowThresholdAndCoalescedRefresh()
    {
        var stream = new ControlledTranslationStream();
        var translations = new RecordingTranslationUseCases(
            (_, _, token) => stream.ReadAsync(token));
        await using var harness = new CoordinatorHarness(
            CreateSettings(translationEnabled: true),
            translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "one two three four");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(TimeSpan.FromMilliseconds(300));
        await harness.DrainAsync();
        Assert.AreEqual(0, translations.RequestCount);

        harness.Time.Advance(TimeSpan.FromMilliseconds(100));
        await harness.WaitForAsync(_ => translations.RequestCount == 1);
        stream.Emit(new TranslationDeltaEvent("你"));
        await harness.DrainAsync();
        Assert.AreEqual(string.Empty, LatestLines(harness.Events).Single().DisplayTranslatedText);

        stream.Emit(new TranslationDeltaEvent("好世界翻译"));
        await harness.WaitForAsync(events => LatestLines(events).Single().DisplayTranslatedText == "你好世界翻译");
        stream.Emit(new TranslationDeltaEvent("继续"));
        await harness.DrainAsync();
        Assert.AreEqual("你好世界翻译", LatestLines(harness.Events).Single().DisplayTranslatedText);

        harness.Time.Advance(TimeSpan.FromMilliseconds(100));
        await harness.WaitForAsync(events =>
            LatestLines(events).Single().DisplayTranslatedText == "你好世界翻译继续");
        stream.Complete();
        await harness.WaitForAsync(events => !LatestLines(events).Single().IsTranslating);

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "one two three four");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, translations.RequestCount, "Exact final input should promote the preview result.");
    }

    [TestMethod]
    public async Task ContinuousLlmPartialStartsAtMaximumPreviewWait()
    {
        var translations = new RecordingTranslationUseCases("预览翻译");
        await using var harness = new CoordinatorHarness(
            CreateSettings(translationEnabled: true),
            translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "one two three four");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        for (var index = 1; index <= 2; index++)
        {
            harness.Time.Advance(TimeSpan.FromMilliseconds(300));
            var text = $"one two three four extension{index}";
            await harness.SendAsync(SpeechRecognitionEventKind.Partial, text);
            await harness.WaitForAsync(events => LatestLines(events)
                .Any(line => line.OriginalText == text));
            Assert.AreEqual(0, translations.RequestCount);
        }

        harness.Time.Advance(TimeSpan.FromMilliseconds(300));
        await harness.WaitForAsync(_ => translations.RequestCount == 1);
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task WallClockRollbackDoesNotDelayMonotonicPreviewScheduling()
    {
        var translations = new RecordingTranslationUseCases("monotonic result");
        await using var harness = new CoordinatorHarness(
            CreateSettings(translationEnabled: true),
            translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "one two three four");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(TimeSpan.FromMilliseconds(300));
        harness.Time.JumpWallClock(TimeSpan.FromHours(-1));
        harness.Time.Advance(TimeSpan.FromMilliseconds(100));

        await harness.WaitForAsync(_ => translations.RequestCount == 1);
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task ReadableActivePreviewIsPromotedAndRemainsLoadingUntilStreamCompletes()
    {
        var stream = new ControlledTranslationStream();
        var translations = new RecordingTranslationUseCases(
            (_, _, token) => stream.ReadAsync(token));
        await using var harness = new CoordinatorHarness(
            CreateSettings(translationEnabled: true),
            translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "one two three four");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(TimeSpan.FromMilliseconds(400));
        await harness.WaitForAsync(_ => translations.RequestCount == 1);
        stream.Emit(new TranslationDeltaEvent("可读取的预览"));
        await harness.WaitForAsync(events => LatestLines(events)
            .Any(line => line.DisplayTranslatedText == "可读取的预览"));

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "one two three four");
        await harness.WaitForAsync(events => LatestLines(events).Single().IsTranslating);
        Assert.AreEqual(1, translations.RequestCount);

        stream.Emit(new TranslationDeltaEvent("完成"));
        stream.Complete();
        await harness.WaitForAsync(events => !LatestLines(events).Single().IsTranslating);
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("可读取的预览完成", LatestLines(harness.Events).Single().DisplayTranslatedText);
    }

    [TestMethod]
    public async Task FastMachineTranslationSendsOnlyCurrentTextAndCompletesImmediately()
    {
        var translations = new RecordingTranslationUseCases("机器译文");
        var settings = CreateSettings(translationEnabled: true) with
        {
            EngineType = 0,
            IsRealTimePreviewEnabled = false
        };
        await using var harness = new CoordinatorHarness(settings, translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Fast machine source.");
        await harness.WaitForAsync(events => LatestLines(events)
            .Any(line => line.DisplayTranslatedText == "机器译文"));
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var invocation = translations.Invocations.Single();
        Assert.AreEqual(TranslationEngineNames.MachineTrans, invocation.Selection!.Engine);
        Assert.AreEqual("Fast machine source.", invocation.Request.Text);
        Assert.AreEqual(1, translations.MaximumActiveStreams);
    }

    [TestMethod]
    public async Task AiTranslationCarriesOnlyTwoPreviousLinesAsReadonlyContext()
    {
        var translations = new RecordingTranslationUseCases("上下文译文");
        await using var harness = new CoordinatorHarness(
            CreateSettings(translationEnabled: true),
            translations);

        var expected = 0;
        foreach (var text in new[] { "First line.", "Second line.", "Current line." })
        {
            expected++;
            await harness.SendAsync(SpeechRecognitionEventKind.Final, text);
            await harness.WaitForAsync(_ => translations.RequestCount >= expected);
            await harness.WaitForAsync(events => LatestLines(events).Count >= expected
                && LatestLines(events).All(line => !line.IsTranslating));
        }
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var currentRequest = translations.Invocations.Last().Request;
        using var json = JsonDocument.Parse(currentRequest.Text);
        Assert.AreEqual("Current line.", json.RootElement.GetProperty("current").GetString());
        var context = json.RootElement.GetProperty("context");
        Assert.AreEqual(2, context.GetArrayLength());
        Assert.AreEqual("First line.", context[0].GetProperty("Original").GetString());
        Assert.AreEqual("Second line.", context[1].GetProperty("Original").GetString());
    }

    [TestMethod]
    public async Task SwitchingFromCanceledSlowLlmStartsMachineTranslationWithoutWaitingOrAcceptingLateOutput()
    {
        var oldLlmStream = new ControlledTranslationStream(ignoreCancellation: true);
        var machineStream = new ControlledTranslationStream();
        var translations = new RecordingTranslationUseCases((index, _, token) =>
            index == 1 ? oldLlmStream.ReadAsync(token) : machineStream.ReadAsync(token));
        await using var harness = new CoordinatorHarness(
            CreateSettings(translationEnabled: true),
            translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "one two three four");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(TimeSpan.FromMilliseconds(400));
        await harness.WaitForAsync(_ => translations.RequestCount == 1);
        oldLlmStream.Emit(new TranslationDeltaEvent("old llm preview"));
        await harness.WaitForAsync(events => LatestLines(events)
            .Single().DisplayTranslatedText == "old llm preview");

        harness.Settings = harness.Settings with
        {
            EngineType = 0,
            EngineId = "machine-test"
        };
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "one two three four");
        await harness.WaitForAsync(_ => translations.RequestCount == 2);
        var machineInvocation = translations.Invocations.Last();
        Assert.AreEqual(TranslationEngineNames.MachineTrans, machineInvocation.Selection!.Engine);
        Assert.AreEqual("one two three four", machineInvocation.Request.Text);
        Assert.AreEqual(2, translations.MaximumActiveStreams);
        machineStream.Emit(new TranslationDeltaEvent("machine translation"));
        machineStream.Complete();
        await harness.WaitForAsync(events => LatestLines(events)
            .Single().DisplayTranslatedText == "machine translation");

        oldLlmStream.Emit(new TranslationDeltaEvent("late old llm translation"));
        oldLlmStream.Complete();
        await harness.DrainAsync();
        Assert.AreEqual(
            "machine translation",
            LatestLines(harness.Events).Single().DisplayTranslatedText);
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task DisablingTranslationCancelsSlowLlmAndQueuedFinalsWithoutRevealingLateOutput()
    {
        var slowLlm = new ControlledTranslationStream(ignoreCancellation: true);
        var translations = new RecordingTranslationUseCases((_, _, token) => slowLlm.ReadAsync(token));
        await using var harness = new CoordinatorHarness(
            CreateSettings(translationEnabled: true),
            translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "one two three four");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(TimeSpan.FromMilliseconds(400));
        await harness.WaitForAsync(_ => translations.RequestCount == 1);
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "one two three four");
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Second queued line.");
        await harness.WaitForAsync(events => LatestLines(events).Count >= 2);

        harness.Settings = harness.Settings with { IsTranslationEnabled = false };
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Third untranslated line.");
        await harness.WaitForAsync(events =>
            LatestLines(events).Count >= 3 && LatestLines(events).All(line => !line.IsTranslating));
        slowLlm.Emit(new TranslationDeltaEvent("late llm translation"));
        await harness.DrainAsync();

        Assert.AreEqual(1, translations.RequestCount);
        Assert.IsTrue(LatestLines(harness.Events)
            .All(line => string.IsNullOrEmpty(line.DisplayTranslatedText)));

        slowLlm.Complete();
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task ReenablingTranslationDoesNotPromoteCanceledLlmPreviewAsFinal()
    {
        var canceledLlm = new ControlledTranslationStream(ignoreCancellation: true);
        var restartedTranslation = new ControlledTranslationStream();
        var translations = new RecordingTranslationUseCases((index, _, token) =>
            index == 1
                ? canceledLlm.ReadAsync(token)
                : restartedTranslation.ReadAsync(token));
        await using var harness = new CoordinatorHarness(
            CreateSettings(translationEnabled: true),
            translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "one two three four");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(TimeSpan.FromMilliseconds(400));
        await harness.WaitForAsync(_ => translations.RequestCount == 1);
        canceledLlm.Emit(new TranslationDeltaEvent("readable canceled preview"));
        await harness.WaitForAsync(events =>
            LatestLines(events).Single().DisplayTranslatedText == "readable canceled preview");

        harness.Settings = harness.Settings with { IsTranslationEnabled = false };
        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "one two three four");
        await harness.WaitForAsync(events => !LatestLines(events).Single().IsTranslating);
        harness.Settings = harness.Settings with { IsTranslationEnabled = true };
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "one two three four");
        await harness.WaitForAsync(events => LatestLines(events).Single().IsTranslating);
        Assert.AreEqual(1, translations.RequestCount, "The canceled provider still owns the physical gate.");

        canceledLlm.Complete();
        await harness.WaitForAsync(_ => translations.RequestCount == 2);
        restartedTranslation.Emit(new TranslationDeltaEvent("fresh final translation"));
        restartedTranslation.Complete();
        await harness.WaitForAsync(events =>
            LatestLines(events).Single().DisplayTranslatedText == "fresh final translation");

        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task RewritingATranslatedPrefixInvalidatesTheOldPreviewAndRevealsTheNewOne()
    {
        var translations = new RecordingTranslationUseCases((index, _, token) =>
            YieldTranslationAsync(index == 1 ? "old readable preview" : "new readable preview", token));
        await using var harness = new CoordinatorHarness(
            CreateSettings(translationEnabled: true),
            translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "turn left now please");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(TimeSpan.FromMilliseconds(400));
        await harness.WaitForAsync(events => LatestLines(events)
            .Single().DisplayTranslatedText == "old readable preview");

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "turn right now please");
        await harness.WaitForAsync(events =>
        {
            var line = LatestLines(events).Single();
            return line.OriginalText == "turn right now please"
                   && line.DisplayTranslatedText.Length == 0;
        });
        harness.Time.Advance(TimeSpan.FromMilliseconds(400));
        await harness.WaitForAsync(events => LatestLines(events)
            .Single().DisplayTranslatedText == "new readable preview");

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "turn right now please");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, translations.RequestCount);
    }

    [TestMethod]
    public async Task NonPrefixFinalRevisionClearsStalePreviewEvenWhenFormalTranslationFails()
    {
        var failedFinal = new ControlledTranslationStream();
        var translations = new RecordingTranslationUseCases((index, _, token) =>
            index == 1
                ? YieldTranslationAsync("old left translation", token)
                : failedFinal.ReadAsync(token));
        await using var harness = new CoordinatorHarness(
            CreateSettings(translationEnabled: true),
            translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "turn left now please");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(TimeSpan.FromMilliseconds(400));
        await harness.WaitForAsync(events =>
            LatestLines(events).Single().DisplayTranslatedText == "old left translation");

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "turn right now please.");
        await harness.WaitForAsync(_ => translations.RequestCount == 2);
        var revised = LatestLines(harness.Events).Single();
        Assert.AreEqual("turn right now please.", revised.OriginalText);
        Assert.AreEqual(string.Empty, revised.DisplayTranslatedText);

        failedFinal.Emit(new TranslationFailedEvent(new Error("test.failure", "formal failed")));
        failedFinal.Complete();
        await harness.WaitForAsync(events => !LatestLines(events).Single().IsTranslating);
        Assert.AreEqual(string.Empty, LatestLines(harness.Events).Single().DisplayTranslatedText);

        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task ExtendingBeyondTheTranslatedPrefixKeepsTheReadablePreview()
    {
        var translations = new RecordingTranslationUseCases("stable readable preview");
        await using var harness = new CoordinatorHarness(
            CreateSettings(translationEnabled: true),
            translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "one two three four");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(TimeSpan.FromMilliseconds(400));
        await harness.WaitForAsync(events => LatestLines(events)
            .Single().DisplayTranslatedText == "stable readable preview");

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "one two three four five");
        await harness.WaitForAsync(events => LatestLines(events)
            .Single().OriginalText == "one two three four five");
        Assert.AreEqual(
            "stable readable preview",
            LatestLines(harness.Events).Single().DisplayTranslatedText);

        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task CanceledSlowPreviewKeepsSingleFlightAndCannotOverwriteNewRevision()
    {
        var oldStream = new ControlledTranslationStream(ignoreCancellation: true);
        var newStream = new ControlledTranslationStream();
        var translations = new RecordingTranslationUseCases((index, _, token) =>
            index == 1 ? oldStream.ReadAsync(token) : newStream.ReadAsync(token));
        await using var harness = new CoordinatorHarness(
            CreateSettings(translationEnabled: true),
            translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "turn left now please");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(TimeSpan.FromMilliseconds(400));
        await harness.WaitForAsync(_ => translations.RequestCount == 1);
        oldStream.Emit(new TranslationDeltaEvent("old readable preview"));
        await harness.WaitForAsync(events => LatestLines(events)
            .Single().DisplayTranslatedText == "old readable preview");

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "turn right now please");
        await harness.WaitForAsync(events => LatestLines(events)
            .Any(line => line.OriginalText == "turn right now please"));
        Assert.AreEqual(string.Empty, LatestLines(harness.Events).Single().DisplayTranslatedText);
        harness.Time.Advance(TimeSpan.FromMilliseconds(400));
        await harness.DrainAsync();
        Assert.AreEqual(1, translations.RequestCount, "Canceled provider must retain the single-flight slot.");

        oldStream.Complete();
        await harness.WaitForAsync(_ => translations.RequestCount == 2);
        newStream.Emit(new TranslationDeltaEvent("正确的新译文"));
        newStream.Complete();
        await harness.WaitForAsync(events =>
            LatestLines(events).Single().DisplayTranslatedText == "正确的新译文");
        Assert.AreEqual(1, translations.MaximumActiveStreams);

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "turn right now please");
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("正确的新译文", LatestLines(harness.Events).Single().DisplayTranslatedText);
    }

    [TestMethod]
    public async Task IdenticalFinalKeepsAnActiveQuietTranslationWithoutRestartingOrExpiringIt()
    {
        var stream = new ControlledTranslationStream();
        var translations = new RecordingTranslationUseCases((_, _, token) => stream.ReadAsync(token));
        var settings = CreateSettings(translationEnabled: true) with
        {
            IsRealTimePreviewEnabled = false,
            AutoClearInterval = 1
        };
        await using var harness = new CoordinatorHarness(settings, translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "one two three four");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(IncrementalSubtitleSegmenter.QuietPeriod + TimeSpan.FromMilliseconds(100));
        await harness.WaitForAsync(_ => translations.RequestCount == 1);
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "one two three four");
        await harness.DrainAsync();
        Assert.AreEqual(1, translations.RequestCount);
        Assert.IsTrue(LatestLines(harness.Events).Single().IsTranslating);

        harness.Time.Advance(TimeSpan.FromSeconds(1.1));
        await harness.DrainAsync();
        Assert.IsFalse(harness.Events.OfType<SpeechFloatingSubtitleRemovedEvent>().Any());
        stream.Emit(new TranslationDeltaEvent("final readable translation"));
        stream.Complete();
        await harness.WaitForAsync(events => !LatestLines(events).Single().IsTranslating);
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        harness.Time.Advance(TimeSpan.FromSeconds(1.1));
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, translations.RequestCount);
    }

    [TestMethod]
    public async Task IdenticalFinalDoesNotRestartTtlAfterQuietTranslationCompleted()
    {
        var translations = new RecordingTranslationUseCases("completed translation");
        var settings = CreateSettings(translationEnabled: true) with
        {
            IsRealTimePreviewEnabled = false,
            AutoClearInterval = 1
        };
        await using var harness = new CoordinatorHarness(settings, translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Partial, "one two three four");
        await harness.WaitForAsync(events => LatestLines(events).Any());
        harness.Time.Advance(IncrementalSubtitleSegmenter.QuietPeriod + TimeSpan.FromMilliseconds(100));
        await harness.WaitForAsync(events => translations.RequestCount == 1
                                             && !LatestLines(events).Single().IsTranslating);

        harness.Time.Advance(TimeSpan.FromMilliseconds(600));
        await harness.SendAsync(SpeechRecognitionEventKind.Final, "one two three four");
        await harness.DrainAsync();
        harness.Time.Advance(TimeSpan.FromMilliseconds(500));
        await harness.WaitForAsync(events => events.OfType<SpeechFloatingSubtitleRemovedEvent>().Any());
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, translations.RequestCount);
    }

    [TestMethod]
    public async Task SharedGatePreventsIgnoredCancellationFromOverlappingTheNextSession()
    {
        var firstStream = new ControlledTranslationStream(ignoreCancellation: true);
        var secondStream = new ControlledTranslationStream();
        var translations = new RecordingTranslationUseCases((index, _, token) =>
            index == 1 ? firstStream.ReadAsync(token) : secondStream.ReadAsync(token));
        using var gate = new SemaphoreSlim(1, 1);
        var settings = CreateSettings(translationEnabled: true) with
        {
            IsRealTimePreviewEnabled = false
        };

        var first = new CoordinatorHarness(settings, translations, gate);
        await first.SendAsync(SpeechRecognitionEventKind.Final, "First session.");
        await first.WaitForAsync(_ => translations.RequestCount == 1);
        await first.DisposeAsync();

        await using var second = new CoordinatorHarness(settings, translations, gate);
        await second.SendAsync(SpeechRecognitionEventKind.Final, "Second session.");
        await second.DrainAsync();
        Assert.AreEqual(1, translations.RequestCount);
        Assert.AreEqual(1, translations.MaximumActiveStreams);

        firstStream.Complete();
        await second.WaitForAsync(_ => translations.RequestCount == 2);
        secondStream.Emit(new TranslationDeltaEvent("second translation"));
        secondStream.Complete();
        await second.WaitForAsync(events => LatestLines(events)
            .Single().DisplayTranslatedText == "second translation");
        await second.SendAsync(SpeechRecognitionEventKind.Stopped);
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, translations.MaximumActiveStreams);
    }

    [TestMethod]
    public async Task TranslationTimeoutStopsLoadingWithoutAppendingErrorText()
    {
        var stream = new ControlledTranslationStream();
        var translations = new RecordingTranslationUseCases(
            (_, _, token) => stream.ReadAsync(token));
        await using var harness = new CoordinatorHarness(
            CreateSettings(translationEnabled: true),
            translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "This translation will time out.");
        await harness.WaitForAsync(_ => translations.RequestCount == 1);
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        harness.Time.Advance(TimeSpan.FromSeconds(15.1));
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var line = LatestLines(harness.Events).Single();
        Assert.IsFalse(line.IsTranslating);
        Assert.AreEqual(string.Empty, line.DisplayTranslatedText);
        Assert.AreEqual("This translation will time out.", line.OriginalText);
    }

    [TestMethod]
    public async Task TranslationTimeoutCompletesEvenWhenTheProviderIgnoresCancellation()
    {
        var stream = new ControlledTranslationStream(ignoreCancellation: true);
        var translations = new RecordingTranslationUseCases((_, _, token) => stream.ReadAsync(token));
        await using var harness = new CoordinatorHarness(
            CreateSettings(translationEnabled: true),
            translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "A provider can remain stuck.");
        await harness.WaitForAsync(_ => translations.RequestCount == 1);
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        harness.Time.Advance(TimeSpan.FromSeconds(15.1));
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var line = LatestLines(harness.Events).Single();
        Assert.IsFalse(line.IsTranslating);
        Assert.AreEqual(string.Empty, line.DisplayTranslatedText);
        stream.Complete();
    }

    [TestMethod]
    public async Task TranslationFailureStopsLoadingWithoutAppendingProviderError()
    {
        var stream = new ControlledTranslationStream();
        var translations = new RecordingTranslationUseCases(
            (_, _, token) => stream.ReadAsync(token));
        await using var harness = new CoordinatorHarness(
            CreateSettings(translationEnabled: true),
            translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Keep only this source.");
        await harness.WaitForAsync(_ => translations.RequestCount == 1);
        stream.Emit(new TranslationFailedEvent(new Error("test.failure", "provider secret error")));
        stream.Complete();
        await harness.WaitForAsync(events => LatestLines(events).Any(line => !line.IsTranslating));
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var line = LatestLines(harness.Events).Single();
        Assert.AreEqual("Keep only this source.", line.OriginalText);
        Assert.AreEqual(string.Empty, line.DisplayTranslatedText);
        Assert.IsFalse(line.OriginalText.Contains("provider secret error", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FinalTranslationQueueOverloadDropsOldestUnstartedJobButKeepsSourceHistory()
    {
        var first = new ControlledTranslationStream();
        var translations = new RecordingTranslationUseCases((index, _, token) =>
            index == 1
                ? first.ReadAsync(token)
                : YieldTranslationAsync($"translation {index}", token));
        var settings = CreateSettings(translationEnabled: true) with
        {
            IsRealTimePreviewEnabled = false,
            MaxFloatingHistory = 40
        };
        await using var harness = new CoordinatorHarness(settings, translations);

        await harness.SendAsync(SpeechRecognitionEventKind.Final, "Source line 1.");
        await harness.WaitForAsync(_ => translations.RequestCount == 1);
        for (var index = 2; index <= 34; index++)
            await harness.SendAsync(SpeechRecognitionEventKind.Final, $"Source line {index}.");
        await harness.WaitForAsync(events => LatestLines(events).Count == 34);
        await harness.SendAsync(SpeechRecognitionEventKind.Stopped);
        first.Complete();
        await harness.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var lines = LatestLines(harness.Events).OrderBy(line => line.Id).ToArray();
        Assert.HasCount(34, lines);
        Assert.AreEqual(33, translations.RequestCount);
        Assert.AreEqual("Source line 2.", lines[1].OriginalText);
        Assert.AreEqual(string.Empty, lines[1].DisplayTranslatedText);
        Assert.AreEqual(1, translations.MaximumActiveStreams);
    }

    private static async IAsyncEnumerable<TranslationEvent> YieldTranslationAsync(
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new TranslationDeltaEvent(text);
        await Task.CompletedTask;
    }

    private static SpeechRecognitionSettings CreateSettings(bool translationEnabled)
    {
        var initial = SettingsTestData.CreateBundle().SpeechRecognition;
        return initial with
        {
            RecognitionLanguage = "en",
            IsTranslationEnabled = translationEnabled,
            IsRealTimePreviewEnabled = translationEnabled,
            TargetLanguage = "zh-Hans",
            EngineId = "test",
            EngineType = 1,
            MaxSentencesPerLine = 1,
            MaxFloatingHistory = 20,
            AutoClearInterval = 0
        };
    }

    private static SpeechSubtitleLine AssertExactlyOneLatestLine(
        IReadOnlyCollection<SpeechSessionEvent> events)
    {
        var lines = LatestLines(events)
            .Where(line => !string.IsNullOrWhiteSpace(line.OriginalText))
            .ToArray();
        Assert.HasCount(1, lines);
        return lines[0];
    }

    private static IReadOnlyList<SpeechSubtitleLine> LatestLines(
        IReadOnlyCollection<SpeechSessionEvent> events) =>
        events.OfType<SpeechSubtitleChangedEvent>()
            .GroupBy(item => item.Subtitle.Id)
            .Select(group => group.Last().Subtitle)
            .ToArray();

    private sealed class CoordinatorHarness : IAsyncDisposable
    {
        private readonly Channel<SpeechRecognitionEvent> _recognition = Channel.CreateUnbounded<SpeechRecognitionEvent>();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly ConcurrentQueue<SpeechSessionEvent> _events = new();

        public CoordinatorHarness(
            SpeechRecognitionSettings settings,
            ITranslationUseCases? translation = null,
            SemaphoreSlim? aiTranslationGate = null,
            SemaphoreSlim? machineTranslationGate = null)
        {
            Time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            Settings = settings;
            long nextId = 0;
            var coordinator = new SubtitleSessionCoordinator(
                () => Settings,
                translation ?? new ImmediateTranslationUseCases(),
                new BuiltInTranslationLanguageCatalog(),
                NullLogger.Instance,
                Time,
                () => Interlocked.Increment(ref nextId),
                item => _events.Enqueue(item),
                aiTranslationGate,
                machineTranslationGate);
            Completion = coordinator.RunAsync(
                _recognition.Reader.ReadAllAsync(_lifetime.Token),
                _lifetime.Token);
        }

        public SpeechRecognitionSettings Settings { get; set; }
        public ManualTimeProvider Time { get; }
        public Task Completion { get; }
        public IReadOnlyCollection<SpeechSessionEvent> Events => _events.ToArray();

        public ValueTask SendAsync(SpeechRecognitionEventKind kind, string? text = null) =>
            _recognition.Writer.WriteAsync(new SpeechRecognitionEvent(kind, text), _lifetime.Token);

        public async Task WaitForAsync(Func<IReadOnlyCollection<SpeechSessionEvent>, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!predicate(Events))
                await Task.Delay(5, timeout.Token);
        }

        public async Task DrainAsync()
        {
            await Task.Yield();
            await Task.Delay(20);
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            try
            {
                await Completion.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }
            _lifetime.Dispose();
        }
    }

    private sealed class ImmediateTranslationUseCases : ITranslationUseCases
    {
        public ITranslationSession Prepare(TranslationProviderSelection? provider = null) =>
            new ImmediateTranslationSession();

        public Task<Result<TranslationResponse>> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TranslationEvent> StreamAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ImmediateTranslationSession : ITranslationSession
    {
        public bool SupportsIdentifiedStreaming => false;

        public Task<TranslationResponse> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationResponse("即时翻译"));

        public async IAsyncEnumerable<TranslationEvent> StreamAsync(
            TranslationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TranslationDeltaEvent("即时翻译");
            await Task.CompletedTask;
        }

        public IAsyncEnumerable<IdentifiedTranslationDelta> StreamIdentifiedAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingTranslationUseCases : ITranslationUseCases
    {
        private readonly Func<int, TranslationRequest, CancellationToken, IAsyncEnumerable<TranslationEvent>> _stream;
        private readonly ConcurrentQueue<TranslationInvocation> _invocations = new();
        private readonly object _activitySync = new();
        private int _nextRequest;
        private int _activeStreams;

        public RecordingTranslationUseCases(string response)
            : this((_, _, token) => Immediate(response, token))
        {
        }

        public RecordingTranslationUseCases(
            Func<int, TranslationRequest, CancellationToken, IAsyncEnumerable<TranslationEvent>> stream)
        {
            _stream = stream;
        }

        public int RequestCount => _invocations.Count;
        public int MaximumActiveStreams { get; private set; }
        public IReadOnlyList<TranslationInvocation> Invocations => _invocations.ToArray();

        public ITranslationSession Prepare(TranslationProviderSelection? provider = null) =>
            new RecordingTranslationSession(this, provider);

        public Task<Result<TranslationResponse>> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TranslationEvent> StreamAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private async IAsyncEnumerable<TranslationEvent> RunAsync(
            TranslationProviderSelection? selection,
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _nextRequest);
            _invocations.Enqueue(new TranslationInvocation(selection, request));
            lock (_activitySync)
            {
                _activeStreams++;
                MaximumActiveStreams = Math.Max(MaximumActiveStreams, _activeStreams);
            }
            try
            {
                await foreach (var item in _stream(index, request, cancellationToken)
                                   .WithCancellation(cancellationToken))
                {
                    yield return item;
                }
            }
            finally
            {
                lock (_activitySync)
                    _activeStreams--;
            }
        }

        private static async IAsyncEnumerable<TranslationEvent> Immediate(
            string response,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TranslationDeltaEvent(response);
            await Task.CompletedTask;
        }

        private sealed class RecordingTranslationSession(
            RecordingTranslationUseCases owner,
            TranslationProviderSelection? selection) : ITranslationSession
        {
            public bool SupportsIdentifiedStreaming => false;

            public Task<TranslationResponse> TranslateAsync(
                TranslationRequest request,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public IAsyncEnumerable<TranslationEvent> StreamAsync(
                TranslationRequest request,
                CancellationToken cancellationToken = default) =>
                owner.RunAsync(selection, request, cancellationToken);

            public IAsyncEnumerable<IdentifiedTranslationDelta> StreamIdentifiedAsync(
                TranslationRequest request,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }
    }

    private sealed record TranslationInvocation(
        TranslationProviderSelection? Selection,
        TranslationRequest Request);

    private sealed class ControlledTranslationStream(bool ignoreCancellation = false)
    {
        private readonly Channel<TranslationEvent> _events = Channel.CreateUnbounded<TranslationEvent>();

        public void Emit(TranslationEvent item) =>
            Assert.IsTrue(_events.Writer.TryWrite(item));

        public void Complete() => _events.Writer.TryComplete();

        public async IAsyncEnumerable<TranslationEvent> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var effectiveToken = ignoreCancellation ? CancellationToken.None : cancellationToken;
            await foreach (var item in _events.Reader.ReadAllAsync(effectiveToken))
                yield return item;
        }
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly DateTimeOffset _start = start;
    private long _timestamp;
    private TimeSpan _wallClockOffset;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
            return _start + TimeSpan.FromTicks(_timestamp) + _wallClockOffset;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_sync)
            return _timestamp;
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (_sync)
        {
            _timers.Add(timer);
            Change(timer, dueTime, period);
        }
        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        long target;
        lock (_sync)
            target = checked(_timestamp + duration.Ticks);

        while (true)
        {
            ManualTimer[] due;
            lock (_sync)
            {
                var next = _timers
                    .Where(timer => !timer.IsDisposed)
                    .Select(timer => timer.NextTimestamp)
                    .DefaultIfEmpty(long.MaxValue)
                    .Min();
                if (next > target)
                {
                    _timestamp = target;
                    return;
                }
                _timestamp = next;
                due = _timers
                    .Where(timer => !timer.IsDisposed && timer.NextTimestamp == next)
                    .ToArray();
                foreach (var timer in due)
                {
                    timer.NextTimestamp = timer.PeriodTicks > 0
                        ? checked(timer.NextTimestamp + timer.PeriodTicks)
                        : long.MaxValue;
                }
            }
            foreach (var timer in due)
                timer.Invoke();
        }
    }

    public void JumpWallClock(TimeSpan offset)
    {
        lock (_sync)
            _wallClockOffset += offset;
    }

    private void Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        timer.PeriodTicks = period == Timeout.InfiniteTimeSpan ? -1 : period.Ticks;
        timer.NextTimestamp = dueTime == Timeout.InfiniteTimeSpan
            ? long.MaxValue
            : checked(_timestamp + Math.Max(0, dueTime.Ticks));
    }

    private void Remove(ManualTimer timer)
    {
        lock (_sync)
            _timers.Remove(timer);
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        public bool IsDisposed { get; private set; }
        public long NextTimestamp { get; set; } = long.MaxValue;
        public long PeriodTicks { get; set; } = -1;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                if (IsDisposed)
                    return false;
                owner.Change(this, dueTime, period);
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._sync)
            {
                if (IsDisposed)
                    return;
                IsDisposed = true;
                owner.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Invoke() => callback(state);
    }
}
