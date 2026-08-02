using EasyChat.Contracts.Speech;
using EasyChat.Infrastructure.Speech.EdgeTts;

namespace EasyChat.Infrastructure.Tests.Speech;

[TestClass]
public sealed class EdgeTtsProviderTests
{
    [TestMethod]
    public async Task SynthesisPreservesRequestAndReturnsMp3Track()
    {
        var transport = new FakeTransport();
        var provider = new EdgeTtsProvider(new FakeCatalog(), transport);
        var request = new TtsSynthesisRequest(
            "hello <world>",
            "en-US-AriaNeural",
            Rate: "+10%",
            Volume: "-5%",
            Pitch: "+2Hz");

        var result = await provider.SynthesizeAsync(request);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("audio/mpeg", result.Value.MediaType);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, result.Value.Content.ToArray());
        Assert.AreSame(request, transport.Request);
    }

    private sealed class FakeCatalog : IEdgeTtsVoiceCatalog
    {
        public ValueTask<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<TtsVoice>>([]);

        public ValueTask<IReadOnlyList<TtsLanguage>> GetLanguagesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<TtsLanguage>>([]);
    }

    private sealed class FakeTransport : IEdgeTtsTransport
    {
        public TtsSynthesisRequest? Request { get; private set; }

        public ValueTask<ReadOnlyMemory<byte>> SynthesizeAsync(
            TtsSynthesisRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1, 2, 3 });
        }
    }
}
