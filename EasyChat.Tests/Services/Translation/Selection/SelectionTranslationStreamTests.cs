using System.Text.Json;
using System.Text.Json.Serialization;
using EasyChat.Models.Translation.Selection;
using EasyChat.Services.Translation.Selection;

namespace EasyChat.Tests.Services.Translation.Selection;

[TestClass]
public class SelectionTranslationStreamTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [TestMethod]
    public void JsonLinesReader_ReassemblesEventsAcrossArbitraryTransportChunks()
    {
        var reader = new JsonLinesStreamReader<SelectionTranslationStreamEvent>(Deserialize);
        var events = new List<SelectionTranslationStreamEvent>();

        events.AddRange(reader.Append("{\"event\":\"start\",\"mode\":\"sent"));
        events.AddRange(reader.Append("ence\"}\n{\"event\":\"translation_delta\",\"text\":\"Hello "));
        events.AddRange(reader.Append("\\\"world\\\"\"}\n{\"event\":\"done\"}"));
        events.AddRange(reader.Complete());

        Assert.AreEqual(3, events.Count);
        Assert.IsInstanceOfType<SelectionTranslationStartedEvent>(events[0]);
        Assert.AreEqual("Hello \"world\"", ((SelectionTranslationDeltaEvent)events[1]).Text);
        Assert.IsInstanceOfType<SelectionTranslationCompletedEvent>(events[2]);
    }

    [TestMethod]
    public void ResultAccumulator_BuildsWordResultFromIncrementalEvents()
    {
        var accumulator = new SelectionTranslationResultAccumulator("schemas");
        var events = new SelectionTranslationStreamEvent[]
        {
            new SelectionTranslationStartedEvent(SelectionTranslationMode.Word),
            new SelectionTranslationSourceDetectedEvent("en"),
            new SelectionTranslationWordHeaderEvent("schema", "/ˈskiːmə/"),
            new SelectionTranslationDefinitionEvent("n.", "模式"),
            new SelectionTranslationFormEvent("复数", "schemas"),
            new SelectionTranslationTipsEvent("常用于描述结构。"),
            new SelectionTranslationExampleEvent("The schema is valid.", "该模式有效。"),
            new SelectionTranslationCompletedEvent()
        };

        foreach (var translationEvent in events)
        {
            accumulator.Apply(translationEvent);
        }

        var result = accumulator.Build() as WordTranslationResult;
        Assert.IsNotNull(result);
        Assert.AreEqual("en", result.DetectedSourceLanguage);
        Assert.AreEqual("schema", result.Word);
        Assert.AreEqual("模式", result.Definitions[0].Meaning);
        Assert.AreEqual("schemas", result.Forms[0].Word);
        Assert.AreEqual("该模式有效。", result.Examples[0].Translation);
    }

    [TestMethod]
    public void SelectionDecoder_EmitsDeltaBeforeJsonObjectCloses()
    {
        var decoder = new SelectionTranslationStreamDecoder(Deserialize);
        var events = new List<SelectionTranslationStreamEvent>();

        events.AddRange(decoder.Append("{\"event\":\"translation_delta\",\"text\":\"第一"));
        Assert.AreEqual("第一", ((SelectionTranslationDeltaEvent)events[0]).Text);

        events.AddRange(decoder.Append("段\\n第二段\"}"));
        Assert.AreEqual("段\n第二段", ((SelectionTranslationDeltaEvent)events[1]).Text);

        events.AddRange(decoder.Append("\n"));
        Assert.AreEqual(2, events.Count);
    }

    private static SelectionTranslationStreamEvent Deserialize(string line)
    {
        return JsonSerializer.Deserialize<SelectionTranslationStreamEvent>(line, JsonOptions)
            ?? throw new JsonException("Expected a structured translation event.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
