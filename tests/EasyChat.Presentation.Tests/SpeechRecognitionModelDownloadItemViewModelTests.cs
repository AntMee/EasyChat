using EasyChat.Contracts.Speech;
using EasyChat.Presentation.Features.Settings;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class SpeechRecognitionModelDownloadItemViewModelTests
{
    [TestMethod]
    public void SyncDownloaded_SetsCompletedProgress()
    {
        var item = new SpeechRecognitionModelDownloadItemViewModel(
            new SpeechRecognitionModelDownloadPackage(
                "zh-CN",
                new Uri("https://example.com/zh-CN.zip")),
            isDownloaded: false);

        item.SyncDownloaded(isDownloaded: true);

        Assert.IsTrue(item.IsDownloaded);
        Assert.AreEqual(1d, item.Progress);
    }

    [TestMethod]
    public void CompleteDownload_ClearsProgressTextImmediately()
    {
        var item = new SpeechRecognitionModelDownloadItemViewModel(
            new SpeechRecognitionModelDownloadPackage(
                "zh-CN",
                new Uri("https://example.com/zh-CN.zip")),
            isDownloaded: false);
        var changedProperties = new List<string?>();
        item.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        item.StartDownload();
        item.SetProgress(1);
        changedProperties.Clear();
        item.CompleteDownload();

        Assert.AreEqual(1d, item.Progress);
        Assert.AreEqual(string.Empty, item.ProgressText);
        CollectionAssert.Contains(changedProperties, nameof(item.ProgressText));
    }
}
