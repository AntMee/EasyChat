using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;
using EasyChat.Presentation.Features.Speech;
using EasyChat.Presentation.Features.Speech.Views;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class SpeechSubtitleProjectionTests
{
    [TestMethod]
    public void TemporaryAndFinalUpdatesWithSameId_ReuseOneItemInBothCollections()
    {
        var projection = new SpeechSubtitleProjection();

        var temporary = projection.Update(CreateLine(5, "draft", string.Empty, isTemporary: true))!;
        var final = projection.Update(CreateLine(5, "final", "translation"))!;
        var repeated = projection.Update(CreateLine(5, "final", "translation"))!;

        Assert.AreSame(temporary, final);
        Assert.AreSame(final, repeated);
        Assert.AreEqual("final", temporary.OriginalText);
        Assert.AreEqual("translation", temporary.TranslatedText);
        Assert.HasCount(1, projection.SubtitleItems);
        Assert.HasCount(1, projection.FloatingSubtitles);
        Assert.AreSame(temporary, projection.SubtitleItems[0]);
        Assert.AreSame(temporary, projection.FloatingSubtitles[0]);
    }

    [TestMethod]
    public void FloatingRemoval_FadesBeforeRemovalAndLateUpdatesRemainInHistoryOnly()
    {
        var projection = new SpeechSubtitleProjection();
        var initial = CreateLine(42, "first", "translated");

        var item = projection.Update(initial)!;
        var fading = projection.BeginFloatingRemoval(initial.Id);

        Assert.AreSame(item, fading);
        Assert.AreEqual(0, item.Opacity);
        CollectionAssert.Contains(projection.FloatingSubtitles, item);

        projection.Update(CreateLine(initial.Id, "corrected", "updated"));

        Assert.AreEqual("corrected", item.OriginalText);
        Assert.AreEqual("updated", item.TranslatedText);
        Assert.HasCount(1, projection.SubtitleItems);
        Assert.HasCount(1, projection.FloatingSubtitles);

        projection.CompleteFloatingRemoval(item);
        projection.Update(CreateLine(initial.Id, "final", "final translation"));

        Assert.AreEqual("final", item.OriginalText);
        Assert.HasCount(1, projection.SubtitleItems);
        Assert.IsEmpty(projection.FloatingSubtitles);
    }

    [TestMethod]
    public void RemovalBeforeFirstUpdate_PreventsLateSubtitleFromEnteringFloatingCollection()
    {
        var projection = new SpeechSubtitleProjection();

        var fading = projection.BeginFloatingRemoval(7);
        var item = projection.Update(CreateLine(7, "late source", "late translation"))!;

        Assert.IsNull(fading);
        CollectionAssert.Contains(projection.SubtitleItems, item);
        Assert.IsEmpty(projection.FloatingSubtitles);
    }

    [TestMethod]
    public void RepeatedRemoval_IsIdempotent()
    {
        var projection = new SpeechSubtitleProjection();
        var item = projection.Update(CreateLine(9, "source", "translation"))!;

        var first = projection.BeginFloatingRemoval(item.Id);
        var repeated = projection.BeginFloatingRemoval(item.Id);
        projection.CompleteFloatingRemoval(item);
        projection.CompleteFloatingRemoval(item);

        Assert.AreSame(item, first);
        Assert.IsNull(repeated);
        Assert.HasCount(1, projection.SubtitleItems);
        Assert.IsEmpty(projection.FloatingSubtitles);
    }

    [TestMethod]
    public void Clear_RemovesHistoryAndFloatingSubtitles()
    {
        var projection = new SpeechSubtitleProjection();
        projection.Update(CreateLine(1, "source", "translation"));

        projection.Clear();

        Assert.IsEmpty(projection.SubtitleItems);
        Assert.IsEmpty(projection.FloatingSubtitles);
    }

    [TestMethod]
    public void ClearDuringFade_RemainsEmptyWhenRemovalCompletes()
    {
        var projection = new SpeechSubtitleProjection();
        var item = projection.Update(CreateLine(11, "source", "translation"))!;
        projection.BeginFloatingRemoval(item.Id);

        projection.Clear();
        projection.CompleteFloatingRemoval(item);

        Assert.IsEmpty(projection.SubtitleItems);
        Assert.IsEmpty(projection.FloatingSubtitles);
    }

    [TestMethod]
    public void FloatingDisplayModes_OnlyAutoScrollModeRequestsScrolling()
    {
        Assert.IsFalse(SubtitleOverlayWindowView.UsesAutoScroll(FloatingDisplayMode.Segmented));
        Assert.IsTrue(SubtitleOverlayWindowView.UsesAutoScroll(FloatingDisplayMode.AutoScroll));
    }

    [TestMethod]
    public void StopLoading_ClearsSpinnerWithoutChangingHistoryOrFloatingMembership()
    {
        var projection = new SpeechSubtitleProjection();
        var item = projection.Update(new SpeechSubtitleLine(
            13,
            TimeSpan.Zero,
            "source",
            string.Empty,
            string.Empty,
            true,
            true))!;

        projection.StopLoading();

        Assert.IsFalse(item.IsTranslating);
        Assert.HasCount(1, projection.SubtitleItems);
        Assert.HasCount(1, projection.FloatingSubtitles);
    }

    [TestMethod]
    public void RetractionBeforeFloatingRemoval_RemovesHistoryAndPreservesOldContentUntilFade()
    {
        var projection = new SpeechSubtitleProjection();
        var item = projection.Update(CreateLine(21, "superseded source", "superseded translation"))!;

        var retracted = projection.Update(CreateLine(item.Id, string.Empty, string.Empty));
        var late = projection.Update(CreateLine(item.Id, "late source", "late translation"));

        Assert.AreSame(item, retracted);
        Assert.IsNull(late);
        Assert.IsEmpty(projection.SubtitleItems);
        Assert.HasCount(1, projection.FloatingSubtitles);
        Assert.AreSame(item, projection.FloatingSubtitles[0]);
        Assert.AreEqual("superseded source", item.OriginalText);
        Assert.AreEqual("superseded translation", item.TranslatedText);
        Assert.AreEqual(1, item.Opacity);

        var fading = projection.BeginFloatingRemoval(item.Id);
        Assert.AreSame(item, fading);
        Assert.AreEqual(0, item.Opacity);
        projection.CompleteFloatingRemoval(item);

        Assert.IsEmpty(projection.SubtitleItems);
        Assert.IsEmpty(projection.FloatingSubtitles);
    }

    [TestMethod]
    public void FloatingRemovalBeforeRetraction_RemovesHistoryAndBlocksUpdatesDuringAndAfterFade()
    {
        var projection = new SpeechSubtitleProjection();
        var item = projection.Update(CreateLine(22, "superseded source", "superseded translation"))!;

        var fading = projection.BeginFloatingRemoval(item.Id);
        var retracted = projection.Update(CreateLine(item.Id, string.Empty, string.Empty));
        var lateDuringFade = projection.Update(CreateLine(item.Id, "late during fade", "late translation"));

        Assert.AreSame(item, fading);
        Assert.AreSame(item, retracted);
        Assert.IsNull(lateDuringFade);
        Assert.IsEmpty(projection.SubtitleItems);
        Assert.HasCount(1, projection.FloatingSubtitles);
        Assert.AreSame(item, projection.FloatingSubtitles[0]);
        Assert.AreEqual("superseded source", item.OriginalText);
        Assert.AreEqual("superseded translation", item.TranslatedText);
        Assert.AreEqual(0, item.Opacity);

        projection.CompleteFloatingRemoval(item);
        var lateAfterFade = projection.Update(CreateLine(item.Id, "late after fade", "late translation"));

        Assert.IsNull(lateAfterFade);
        Assert.IsEmpty(projection.SubtitleItems);
        Assert.IsEmpty(projection.FloatingSubtitles);
    }

    private static SpeechSubtitleLine CreateLine(
        long id,
        string original,
        string translated,
        bool isTemporary = false) =>
        new(
            id,
            TimeSpan.FromSeconds(id),
            original,
            translated,
            translated,
            false,
            isTemporary);
}
