using MicroASR;

namespace EasyChat.Infrastructure.Tests.Speech;

[TestClass]
public sealed class DigitalSilenceEndpointDetectorTests
{
    [TestMethod]
    public void ThirtyFiveTwentyMillisecondFramesReachTheProductionEndpoint()
    {
        const int samplesPerFrame = 320;
        var detector = new StreamingRecognizer.DigitalSilenceEndpointDetector(11_200);
        var frame = new byte[samplesPerFrame * sizeof(short)];

        for (var index = 0; index < 34; index++)
        {
            Assert.IsFalse(detector.TryFindEndpoint(frame, out var consumedByteCount));
            Assert.AreEqual(frame.Length, consumedByteCount);
        }

        Assert.IsTrue(detector.TryFindEndpoint(frame, out var endpointByteCount));
        Assert.AreEqual(frame.Length, endpointByteCount);
    }

    [TestMethod]
    public void ExactZeroPcmReachesEndpointAcrossChunks()
    {
        var detector = new StreamingRecognizer.DigitalSilenceEndpointDetector(5);

        Assert.IsFalse(detector.TryFindEndpoint(new byte[6], out var firstByteCount));
        Assert.AreEqual(6, firstByteCount);
        Assert.IsTrue(detector.TryFindEndpoint(new byte[4], out var endpointByteCount));
        Assert.AreEqual(4, endpointByteCount);
    }

    [TestMethod]
    public void NonZeroSampleRestartsRequiredSilence()
    {
        var detector = new StreamingRecognizer.DigitalSilenceEndpointDetector(4);

        Assert.IsFalse(detector.TryFindEndpoint(new byte[6], out _));
        Assert.IsFalse(detector.TryFindEndpoint(new byte[] { 1, 0, 0, 0, 0, 0 }, out _));
        Assert.IsTrue(detector.TryFindEndpoint(new byte[4], out _));
    }

    [TestMethod]
    public void ResetRequiresANewCompleteSilenceWindow()
    {
        var detector = new StreamingRecognizer.DigitalSilenceEndpointDetector(2);

        Assert.IsTrue(detector.TryFindEndpoint(new byte[4], out _));
        detector.Reset();

        Assert.IsFalse(detector.TryFindEndpoint(new byte[2], out _));
        Assert.IsTrue(detector.TryFindEndpoint(new byte[2], out _));
    }

    [TestMethod]
    public void EndpointStopsAtFirstBoundaryBeforeAudioResumesInTheSameChunk()
    {
        var detector = new StreamingRecognizer.DigitalSilenceEndpointDetector(4);
        byte[] pcm = [0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 2, 0];

        Assert.IsTrue(detector.TryFindEndpoint(pcm, out var endpointByteCount));
        Assert.AreEqual(8, endpointByteCount);

        detector.Reset();
        Assert.IsFalse(detector.TryFindEndpoint(pcm.AsSpan(endpointByteCount), out var tailByteCount));
        Assert.AreEqual(4, tailByteCount);
    }

    [TestMethod]
    public void EndpointInCurrentChunkIncludesOnlySamplesNeededAfterPreviousSilence()
    {
        var detector = new StreamingRecognizer.DigitalSilenceEndpointDetector(4);

        Assert.IsFalse(detector.TryFindEndpoint(new byte[6], out _));
        byte[] resumed = [0, 0, 1, 0, 2, 0];

        Assert.IsTrue(detector.TryFindEndpoint(resumed, out var endpointByteCount));
        Assert.AreEqual(2, endpointByteCount);
    }
}

[TestClass]
public sealed class StreamingRecognizerFinalDecisionTests
{
    [TestMethod]
    public void ShortSpeechUsesNonEmptyHypothesisProducedByFlush()
    {
        var text = StreamingRecognizer.ResolveFinalText(
            minimumSpeechMet: false,
            finalHypothesis: "Final from flush.",
            publishedPartial: string.Empty);

        Assert.AreEqual("Final from flush.", text);
    }

    [TestMethod]
    public void ShortSpeechFallsBackToPublishedPartialWhenFlushIsEmpty()
    {
        var text = StreamingRecognizer.ResolveFinalText(
            minimumSpeechMet: false,
            finalHypothesis: string.Empty,
            publishedPartial: "Published draft");

        Assert.AreEqual("Published draft", text);
    }

    [TestMethod]
    public void FlushHypothesisTakesPrecedenceOverPublishedPartial()
    {
        var text = StreamingRecognizer.ResolveFinalText(
            minimumSpeechMet: true,
            finalHypothesis: "Corrected final.",
            publishedPartial: "Old draft");

        Assert.AreEqual("Corrected final.", text);
    }

    [TestMethod]
    public void EmptyRecognitionDoesNotCreateFinalText()
    {
        var text = StreamingRecognizer.ResolveFinalText(
            minimumSpeechMet: false,
            finalHypothesis: string.Empty,
            publishedPartial: string.Empty);

        Assert.AreEqual(string.Empty, text);
    }
}
