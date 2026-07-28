using System.Text.Json;
using EasyChat.Models.Translation;
using EasyChat.Services.Streaming;

namespace EasyChat.Tests.Services.Streaming;

[TestClass]
public sealed class JsonLinesEventStreamDecoderTests
{
    [TestMethod]
    public void Decoder_PreservesIdsAcrossTransportChunks()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var decoder = new JsonLinesEventStreamDecoder<IdentifiedTranslationStreamEvent>(
            line => JsonSerializer.Deserialize<IdentifiedTranslationStreamEvent>(line, options)!);
        var events = new List<IdentifiedTranslationStreamEvent>();

        events.AddRange(decoder.Append("{\"event\":\"translation_delta\",\"id\":\"blo"));
        events.AddRange(decoder.Append("ck-0\",\"text\":\"\u767d\u5bab\"}\n{\"event\":\"translation_delta\","));
        events.AddRange(decoder.Append("\"id\":\"block-1\",\"text\":\"\u534e\u76db\u987f\"}"));
        events.AddRange(decoder.Complete());

        var deltas = events.OfType<IdentifiedTranslationDeltaEvent>().ToArray();
        Assert.HasCount(2, deltas);
        Assert.AreEqual("block-0", deltas[0].Id);
        Assert.AreEqual("\u767d\u5bab", deltas[0].Text);
        Assert.AreEqual("block-1", deltas[1].Id);
        Assert.AreEqual("\u534e\u76db\u987f", deltas[1].Text);
    }
}
