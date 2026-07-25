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

    private static TextAssistStreamEvent Deserialize(string line)
    {
        return JsonSerializer.Deserialize<TextAssistStreamEvent>(line, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new JsonException("Expected event.");
    }
}
