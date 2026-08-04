using EasyChat.Application.Speech;

namespace EasyChat.Application.Tests.Speech;

[TestClass]
public sealed class IncrementalSubtitleSegmenterTests
{
    private static readonly TimeSpan Epoch = TimeSpan.Zero;

    [TestMethod]
    public void NormalizationAndMeasurementsUseNfcGraphemesAndDisplayColumns()
    {
        var normalized = IncrementalSubtitleSegmenter.Normalize("  Cafe\u0301   👨‍👩‍👧‍👦 你好  ");

        Assert.AreEqual("Café 👨‍👩‍👧‍👦 你好", normalized);
        Assert.AreEqual(9, IncrementalSubtitleSegmenter.CountGraphemes(normalized));
        Assert.IsGreaterThan(
            IncrementalSubtitleSegmenter.CountGraphemes(normalized),
            IncrementalSubtitleSegmenter.CountDisplayColumns(normalized));
    }

    [TestMethod]
    public void StableStrongBoundaryIgnoresDecimalUrlEmailAndAbbreviationDots()
    {
        const string text = "Dr. Smith paid 3.14 at example.com and a@b.com. Next";
        var segmenter = new IncrementalSubtitleSegmenter();

        _ = segmenter.ApplyPartial(text, Epoch);
        var update = segmenter.ApplyPartial(text, Epoch + TimeSpan.FromMilliseconds(100));

        Assert.HasCount(1, update.Commits);
        Assert.AreEqual("Dr. Smith paid 3.14 at example.com and a@b.com.", update.Commits[0].Text);
        Assert.AreEqual("Next", update.DraftText);
    }

    [TestMethod]
    public void StableLongEnglishHypothesisRetainsTwoRevisionWords()
    {
        var text = string.Join(" ", Enumerable.Range(1, 20).Select(index => $"word{index}"));
        var segmenter = new IncrementalSubtitleSegmenter();

        _ = segmenter.ApplyPartial(text, Epoch);
        var update = segmenter.ApplyPartial(text, Epoch + TimeSpan.FromMilliseconds(100));

        Assert.IsNotEmpty(update.Commits);
        Assert.IsTrue(update.Commits[0].CloseLine);
        Assert.IsGreaterThanOrEqualTo(
            2,
            IncrementalSubtitleSegmenter.CountWords(update.DraftText));
        Assert.AreEqual(
            text,
            Join(update.Commits.Select(commit => commit.Text).Append(update.DraftText)));
    }

    [TestMethod]
    public void StableCjkHypothesisRetainsFourRevisionGraphemes()
    {
        const string text = "这是一个用于测试连续语音识别字幕切分算法是否稳定可靠的中文长句子没有任何标点符号";
        var segmenter = new IncrementalSubtitleSegmenter();

        _ = segmenter.ApplyPartial(text, Epoch);
        var update = segmenter.ApplyPartial(text, Epoch + TimeSpan.FromMilliseconds(100));

        Assert.IsNotEmpty(update.Commits);
        Assert.IsGreaterThanOrEqualTo(
            4,
            IncrementalSubtitleSegmenter.CountGraphemes(update.DraftText));
        Assert.AreEqual(text, string.Concat(update.Commits.Select(commit => commit.Text)) + update.DraftText);
    }

    [TestMethod]
    public void FourSecondsWithoutStablePrefixForcesAGraphemeSafeCut()
    {
        const string first = "one two three four five six seven eight nine ten eleven twelve thirteen fourteen";
        const string changed = first + " fifteen";
        var segmenter = new IncrementalSubtitleSegmenter();

        _ = segmenter.ApplyPartial(first, Epoch);
        var update = segmenter.ApplyPartial(changed, Epoch + IncrementalSubtitleSegmenter.HardSegmentDuration);

        Assert.IsNotEmpty(update.Commits);
        Assert.IsTrue(update.Commits[0].CloseLine);
        Assert.AreEqual(changed, Join(update.Commits.Select(commit => commit.Text).Append(update.DraftText)));
    }

    [TestMethod]
    public void HardCutDoesNotSplitACombiningTextElement()
    {
        const string first = "abcdefghijklmnopqa";
        const string changed = "abcdefghijklmnopqa\u20dd";
        var segmenter = new IncrementalSubtitleSegmenter();

        _ = segmenter.ApplyPartial(first, Epoch);
        var update = segmenter.ApplyPartial(
            changed,
            Epoch + IncrementalSubtitleSegmenter.HardSegmentDuration);

        Assert.HasCount(1, update.Commits);
        Assert.AreEqual(changed, update.Commits[0].Text);
        Assert.AreEqual(string.Empty, update.DraftText);
    }

    [TestMethod]
    public void QuietPeriodSealsTheVisibleDraft()
    {
        var segmenter = new IncrementalSubtitleSegmenter();
        _ = segmenter.ApplyPartial("a quiet unfinished thought", Epoch);

        var early = segmenter.Tick(
            Epoch + IncrementalSubtitleSegmenter.QuietPeriod - TimeSpan.FromMilliseconds(1));
        var quiet = segmenter.Tick(Epoch + IncrementalSubtitleSegmenter.QuietPeriod);

        Assert.IsEmpty(early.Commits);
        Assert.HasCount(1, quiet.Commits);
        Assert.AreEqual("a quiet unfinished thought", quiet.Commits[0].Text);
        Assert.IsTrue(quiet.CloseCurrentLine);
    }

    [TestMethod]
    public void FinalCarriesOldAndReplacementHypothesesForRangeReconciliation()
    {
        var segmenter = new IncrementalSubtitleSegmenter();
        _ = segmenter.ApplyPartial("turn left at the old bridge", Epoch);

        var update = segmenter.ApplyFinal("turn right at the new bridge.", Epoch + TimeSpan.FromSeconds(1));

        Assert.IsTrue(update.ReconcileFinal);
        Assert.AreEqual("turn left at the old bridge", update.PreviousHypothesis);
        Assert.AreEqual("turn right at the new bridge.", update.Hypothesis);
        Assert.IsTrue(update.EndsUtterance);
    }

    [TestMethod]
    public void CompleteLatestCarriesReconciliationMetadataForStoppedRecognition()
    {
        const string initial = "I like cats. and dogs";
        const string latest = "I like bats. and dogs";
        var segmenter = new IncrementalSubtitleSegmenter();
        _ = segmenter.ApplyPartial(initial, Epoch);
        _ = segmenter.ApplyPartial(initial, Epoch + TimeSpan.FromMilliseconds(100));
        _ = segmenter.ApplyPartial(latest, Epoch + TimeSpan.FromMilliseconds(200));

        var update = segmenter.CompleteLatest();

        Assert.IsTrue(update.ReconcileFinal);
        Assert.AreEqual(latest, update.PreviousHypothesis);
        Assert.AreEqual(latest, update.Hypothesis);
        Assert.IsTrue(update.EndsUtterance);
    }

    [TestMethod]
    public void FinalTerminalSuffixIncludesClosingPunctuation()
    {
        const string partial = "他说“你好";
        const string final = "他说“你好。”";
        var segmenter = new IncrementalSubtitleSegmenter();
        _ = segmenter.ApplyPartial(partial, Epoch);
        _ = segmenter.Tick(Epoch + IncrementalSubtitleSegmenter.QuietPeriod);

        var update = segmenter.ApplyFinal(
            final,
            Epoch + IncrementalSubtitleSegmenter.QuietPeriod + TimeSpan.FromMilliseconds(100));

        Assert.AreEqual("。”", update.AppendToPreviousLine);
        Assert.IsTrue(update.ReconcileFinal);
        Assert.AreEqual(final, update.Hypothesis);
    }

    [TestMethod]
    public void StrongBoundaryKeepsTerminalClustersAndClosingQuotesTogether()
    {
        const string text = "Really?! “你好。” Next";
        var segmenter = new IncrementalSubtitleSegmenter();
        _ = segmenter.ApplyPartial(text, Epoch);

        var update = segmenter.ApplyPartial(text, Epoch + TimeSpan.FromMilliseconds(100));

        CollectionAssert.AreEqual(
            new[] { "Really?!", "“你好。”" },
            update.Commits.Select(commit => commit.Text).ToArray());
        Assert.AreEqual("Next", update.DraftText);
    }

    private static string Join(IEnumerable<string> parts) =>
        string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}
