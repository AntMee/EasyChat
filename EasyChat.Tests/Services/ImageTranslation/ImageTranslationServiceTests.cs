using Avalonia;
using EasyChat.Models.Ocr;
using EasyChat.Services.ImageTranslation;

namespace EasyChat.Tests.Services.ImageTranslation;

[TestClass]
public sealed class ImageTranslationServiceTests
{
    [TestMethod]
    public void GroupRegions_MergesAdjacentRegionsOnSameLine()
    {
        var regions = new[]
        {
            Region("Hello", 10, 10, 42, 16),
            Region("world", 58, 11, 42, 15),
            Region("Next", 10, 48, 35, 16)
        };

        var blocks = ImageTranslationService.GroupRegions(regions);

        Assert.HasCount(2, blocks);
        Assert.AreEqual("Hello world", blocks[0].SourceText);
        Assert.AreEqual("Next", blocks[1].SourceText);
        Assert.AreEqual(10, blocks[0].Bounds.Left);
        Assert.AreEqual(100, blocks[0].Bounds.Right);
    }

    [TestMethod]
    public void GroupRegions_PreservesReadingOrder()
    {
        var regions = new[]
        {
            Region("second", 120, 60, 50, 18),
            Region("first", 20, 10, 45, 18),
            Region("third", 20, 95, 45, 18)
        };

        var blocks = ImageTranslationService.GroupRegions(regions);

        CollectionAssert.AreEqual(new[] { "first", "second", "third" }, blocks.Select(block => block.SourceText).ToArray());
    }

    [TestMethod]
    public void OcrRecognitionResult_ExposesOrderedTextAndBounds()
    {
        var region = Region("text", 12, 24, 30, 10);
        var result = new OcrRecognitionResult([region]);

        Assert.AreEqual("text", result.Text);
        Assert.AreEqual(new Rect(12, 24, 30, 10), region.Bounds);
    }

    [TestMethod]
    public void PixelToDipScale_ConvertsPhysicalPixelsToAvaloniaDips()
    {
        var scale = ImageTranslationService.PixelToDipScale(new PixelSize(200, 100), new Size(100, 50));

        Assert.AreEqual(0.5, scale.X, 0.0001);
        Assert.AreEqual(0.5, scale.Y, 0.0001);
    }

    [TestMethod]
    public void CalculateTextAngle_UsesLongestPolygonEdge()
    {
        var horizontal = new[]
        {
            new Point(10, 10), new Point(110, 10), new Point(110, 30), new Point(10, 30)
        };
        var vertical = new[]
        {
            new Point(10, 10), new Point(30, 10), new Point(30, 110), new Point(10, 110)
        };

        Assert.AreEqual(0, OcrTextRegion.CalculateTextAngle(horizontal), 0.001);
        Assert.AreEqual(90, OcrTextRegion.CalculateTextAngle(vertical), 0.001);
    }

    [TestMethod]
    public void ParseBatchTranslations_MapsResultsByStableId()
    {
        const string response = """
            [{"id":"block-0","translation":"电子邮箱地址"},{"id":"block-1","translation":"华盛顿"}]
            """;

        var translations = ImageTranslationService.ParseBatchTranslations(response);

        Assert.HasCount(2, translations);
        Assert.AreEqual("电子邮箱地址", translations["block-0"]);
        Assert.AreEqual("华盛顿", translations["block-1"]);
    }

    [TestMethod]
    public void ParseBatchTranslations_AcceptsJsonSurroundedByModelText()
    {
        const string response = "result: [{\"id\":\"block-0\",\"translation\":\"邮箱\"}] done";

        var translations = ImageTranslationService.ParseBatchTranslations(response);

        Assert.AreEqual("邮箱", translations["block-0"]);
    }

    [TestMethod]
    public void ParseBatchTranslations_AcceptsWrappedAndNdjsonResults()
    {
        const string wrapped = "{\"translations\":[{\"id\":\"block-0\",\"translation\":\"邮箱\"}]}";
        const string ndjson = "{\"id\":\"block-1\",\"translation\":\"华盛顿\"}\n{\"id\":\"block-2\",\"translation\":\"地址\"}";

        var wrappedTranslations = ImageTranslationService.ParseBatchTranslations(wrapped);
        var ndjsonTranslations = ImageTranslationService.ParseBatchTranslations(ndjson);

        Assert.AreEqual("邮箱", wrappedTranslations["block-0"]);
        Assert.AreEqual("华盛顿", ndjsonTranslations["block-1"]);
        Assert.AreEqual("地址", ndjsonTranslations["block-2"]);
    }

    [TestMethod]
    public void ParseBatchTranslations_UnwrapsRuntimeTranslationEvents()
    {
        const string response = """
            {"event":"translation_delta","text":"[{\"id\":\"block-0\",\"translation\":\"邮箱\"}"}
            {"event":"translation_delta","text":",{\"id\":\"block-1\",\"translation\":\"地址\"}]"}
            {"event":"done"}
            """;

        var translations = ImageTranslationService.ParseBatchTranslations(response);

        Assert.AreEqual("邮箱", translations["block-0"]);
        Assert.AreEqual("地址", translations["block-1"]);
    }

    [TestMethod]
    public void CreateRegionBlocks_PreservesEveryOcrBox()
    {
        var regions = new[]
        {
            Region("first", 20, 30, 100, 12),
            Region("second", 20, 60, 80, 18)
        };

        var blocks = ImageTranslationService.CreateRegionBlocks(regions);

        Assert.HasCount(2, blocks);
        Assert.HasCount(1, blocks[0].Regions);
        Assert.AreEqual(100, blocks[0].BoxWidth, 0.001);
        Assert.AreEqual(12, blocks[0].BoxHeight, 0.001);
    }

    [TestMethod]
    public void CalculatePreferredFontSize_UsesOriginalTextHeight()
    {
        var original = new Rect(20, 30, 120, 20);

        var fontSize = ImageTranslationService.CalculatePreferredFontSize(original, 0);

        Assert.AreEqual(14.4, fontSize, 0.001);
    }

    [TestMethod]
    public void IsLayoutWithinBox_RequiresBothDimensionsToFitExactly()
    {
        Assert.IsTrue(ImageTranslationService.IsLayoutWithinBox(100, 20, 100, 20));
        Assert.IsFalse(ImageTranslationService.IsLayoutWithinBox(100.01, 20, 100, 20));
        Assert.IsFalse(ImageTranslationService.IsLayoutWithinBox(100, 20.01, 100, 20));
    }

    [TestMethod]
    public void OcrRegion_UsesPolygonEdgesForRotatedBoxSize()
    {
        var region = new OcrTextRegion("rotated",
            [
                new Point(0, 0),
                new Point(70.7107, 70.7107),
                new Point(56.5686, 84.8528),
                new Point(-14.1421, 14.1421)
            ],
            45);

        Assert.AreEqual(100, region.OrientedSize.Width, 0.01);
        Assert.AreEqual(20, region.OrientedSize.Height, 0.01);
    }

    private static OcrTextRegion Region(string text, double x, double y, double width, double height)
        => new(text,
            [
                new Point(x, y),
                new Point(x + width, y),
                new Point(x + width, y + height),
                new Point(x, y + height)
            ],
            0);

}
