using System.Text.Json;
using EasyChat.Models.Translation.TextAssist;
using EasyChat.Services.Streaming;

namespace EasyChat.Tests.Services.TextAssist;

[TestClass]
public sealed class TextAssistStreamTests
{
    [TestMethod]
    public void Decoder_ReassemblesCorrectedDeltaAcrossChunks()
    {
        var decoder = new JsonLinesDeltaStreamDecoder<TextAssistStreamEvent>(Deserialize, "corrected_delta", "text");
        var events = new List<TextAssistStreamEvent>();
        events.AddRange(decoder.Append("{\"event\":\"start\",\"mode\":\"correction\",\"sourceLanguage\":\"en\",\"targetLanguage\":null}\n{\"event\":\"corrected_delta\",\"text\":\"first "));
        events.AddRange(decoder.Append("part\\nsecond\"}\n{\"event\":\"done\"}"));
        events.AddRange(decoder.Complete());

        Assert.IsTrue(events.Any(x => x is TextAssistStartedEvent));
        Assert.AreEqual("first part\nsecond", string.Concat(events.OfType<TextAssistCorrectedDeltaEvent>().Select(x => x.Text)));
        Assert.IsTrue(events.Last() is TextAssistCompletedEvent);
    }

    [TestMethod]
    public void CorrectionAccumulator_TracksIssuesAndRejectsOutOfRange()
    {
        var accumulator = new TextAssistCorrectionAccumulator(5);
        accumulator.Apply(new TextAssistStartedEvent("correction", "en", null));
        accumulator.Apply(new TextAssistIssueEvent(1, 2, "grammar", "Wrong", "Right"));
        accumulator.Apply(new TextAssistIssueEvent(5, 2, "grammar", "Invalid", "Invalid"));
        accumulator.Apply(new TextAssistCorrectedDeltaEvent("fixed"));
        accumulator.Apply(new TextAssistCompletedEvent());
        accumulator.EnsureComplete();

        Assert.AreEqual(1, accumulator.Issues.Count);
        Assert.AreEqual("fixed", accumulator.CorrectedText);
    }

    [TestMethod]
    public void StartedEvent_AcceptsCorrectionLanguageAlias()
    {
        var started = Deserialize("{\"event\":\"start\",\"mode\":\"correction\",\"language\":\"en\"}") as TextAssistStartedEvent;
        Assert.IsNotNull(started);
        Assert.AreEqual("en", started.SourceLanguage);
    }

    [TestMethod]
    public void CorrectionAccumulator_AllowsDeltaOnlyStreamAtEndOfResponse()
    {
        var accumulator = new TextAssistCorrectionAccumulator(4);
        accumulator.Apply(new TextAssistCorrectedDeltaEvent("fixed"));
        accumulator.CompleteImplicitly();

        accumulator.EnsureComplete();

        Assert.AreEqual("fixed", accumulator.CorrectedText);
    }

    [TestMethod]
    public void CorrectionAccumulator_AllowsIssueOnlyResponseWithoutStartEvent()
    {
        var accumulator = new TextAssistCorrectionAccumulator(4);
        accumulator.Apply(new TextAssistIssueEvent(0, 1, "grammar", "Wrong", "Right"));
        accumulator.Apply(new TextAssistCompletedEvent());

        accumulator.EnsureComplete();

        Assert.HasCount(1, accumulator.Issues);
    }

    [TestMethod]
    public void Decoder_PreservesCorrectionVariantWhileStreaming()
    {
        var decoder = new JsonLinesDeltaStreamDecoder<TextAssistStreamEvent>(Deserialize, "corrected_delta", "text");
        var events = decoder.Append("{\"event\":\"corrected_delta\",\"variant\":2,\"text\":\"alternative\"}").ToArray();

        var delta = events.OfType<TextAssistCorrectedDeltaEvent>().Single();
        Assert.AreEqual(2, delta.Variant);
        Assert.AreEqual("alternative", delta.Text);
    }

    [TestMethod]
    public void Decoder_ParsesDetailedTranslationAnnotation()
    {
        var decoder = new JsonLinesDeltaStreamDecoder<TextAssistStreamEvent>(Deserialize, "translation_delta", "text");
        var events = decoder.Append("{\"event\":\"annotation\",\"term\":\"break the ice\",\"category\":\"collocation\",\"meaning\":\"打破僵局\",\"note\":\"固定搭配\",\"relatedTerms\":[\"icebreaker\"]}\n").ToArray();

        var annotation = events.OfType<TextAssistTranslationAnnotationEvent>().Single();
        Assert.AreEqual("break the ice", annotation.Term);
        Assert.AreEqual("打破僵局", annotation.Meaning);
        Assert.AreEqual("icebreaker", annotation.RelatedTerms!.Single());
        Assert.IsTrue(annotation.HasRelatedTerms);
    }

    [TestMethod]
    public void Decoder_ParsesPolishedTextAndExplanation()
    {
        var decoder = new JsonLinesDeltaStreamDecoder<TextAssistStreamEvent>(Deserialize, "translation_delta", "text");
        var events = new List<TextAssistStreamEvent>();
        events.AddRange(decoder.Append("{\"event\":\"translation_delta\",\"text\":\"More natu"));
        events.AddRange(decoder.Append("ral text.\"}\n{\"event\":\"polish_explanation\",\"category\":\"Clarity\",\"original\":\"old text\",\"revised\":\"natural text\",\"explanation\":\"Uses a clearer expression.\"}\n{\"event\":\"done\"}"));
        events.AddRange(decoder.Complete());

        Assert.AreEqual("More natural text.", string.Concat(events.OfType<TextAssistTranslationDeltaEvent>().Select(x => x.Text)));
        var explanation = events.OfType<TextAssistPolishExplanationEvent>().Single();
        Assert.AreEqual("Clarity", explanation.Category);
        Assert.AreEqual("old text", explanation.Original);
        Assert.AreEqual("natural text", explanation.Revised);
        Assert.IsTrue(explanation.HasOriginal);
        Assert.IsTrue(explanation.HasRevised);
        Assert.IsTrue(events.Last() is TextAssistCompletedEvent);
    }

    [TestMethod]
    public void CorrectionAccumulator_AssociatesTranslationsByVariant()
    {
        var accumulator = new TextAssistCorrectionAccumulator(4);
        accumulator.Apply(new TextAssistCorrectedDeltaEvent("first", 1));
        accumulator.Apply(new TextAssistCorrectedDeltaEvent("other", 2));
        accumulator.Apply(new TextAssistCorrectionTranslationDeltaEvent("第一", 1));
        accumulator.Apply(new TextAssistCorrectionTranslationDeltaEvent("其他", 2));

        Assert.AreEqual("first", accumulator.CorrectedVariants[1]);
        Assert.AreEqual("other", accumulator.CorrectedVariants[2]);
        Assert.AreEqual("第一", accumulator.CorrectedTranslations[1]);
        Assert.AreEqual("其他", accumulator.CorrectedTranslations[2]);
    }

    private static TextAssistStreamEvent Deserialize(string line)
    {
        return JsonSerializer.Deserialize<TextAssistStreamEvent>(line, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new JsonException("Expected event.");
    }
}
