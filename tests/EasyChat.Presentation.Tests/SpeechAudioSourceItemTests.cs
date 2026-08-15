using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Features.Speech;
using EasyChat.Presentation.Lang;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class SpeechAudioSourceItemTests
{
    [TestMethod]
    public void MicrophoneSourcesExposeLocalizedPhysicalAndVirtualCategories()
    {
        var physical = new SpeechAudioSourceItem(
            new AudioCaptureSourceDescriptor(
                new AudioCaptureSourceToken("test:physical"),
                AudioCaptureSourceKind.Microphone,
                "Microphone",
                "Microphone",
                null,
                ReadOnlyMemory<byte>.Empty),
            isSelected: false);
        var cable = new SpeechAudioSourceItem(
            new AudioCaptureSourceDescriptor(
                new AudioCaptureSourceToken("test:cable"),
                AudioCaptureSourceKind.Microphone,
                "CABLE Output",
                "CABLE Output",
                null,
                ReadOnlyMemory<byte>.Empty,
                IsVirtualCable: true),
            isSelected: false);

        Assert.AreEqual(Resources.Speech_PhysicalMicrophone, physical.Category);
        Assert.AreEqual(Resources.Speech_VirtualMicrophone, cable.Category);
        Assert.IsTrue(physical.HasCategory);
        Assert.IsTrue(cable.HasCategory);
    }
}
