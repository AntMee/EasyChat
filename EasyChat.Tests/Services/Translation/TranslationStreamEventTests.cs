using System.Text.Json;
using EasyChat.Models.Translation;
using EasyChat.Services.Streaming;

namespace EasyChat.Tests.Services.Translation;

[TestClass]
public sealed class TranslationStreamEventTests
{
    [TestMethod]
    public void Decoder_ReadsStructuredTranslationEvents()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var decoder = new JsonLinesDeltaStreamDecoder<TranslationStreamEvent>(
            line => JsonSerializer.Deserialize<TranslationStreamEvent>(line, options)!,
            "translation_delta",
            "text");

        var events = decoder.Append(
                "{\"event\":\"start\",\"mode\":\"translation\",\"source_language\":\"English\",\"target_language\":\"Chinese\"}\n" +
                "{\"event\":\"translation_delta\",\"text\":\"你好\"}\n" +
                "{\"event\":\"done\"}\n")
            .Concat(decoder.Complete())
            .ToList();

        Assert.AreEqual(3, events.Count);
        Assert.IsInstanceOfType<TranslationStartedEvent>(events[0]);
        Assert.AreEqual("你好", ((TranslationDeltaEvent)events[1]).Text);
        Assert.IsInstanceOfType<TranslationCompletedEvent>(events[2]);
    }
}
