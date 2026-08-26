using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Ocr;
using EasyChat.Presentation.ImageTranslation;

namespace EasyChat.Presentation.Tests.ImageTranslation;

[TestClass]
public sealed class ImageTextAlignmentAnalyzerTests
{
    [TestMethod]
    public void InferAlignment_DefaultsIsolatedTextToCenter()
    {
        var alignment = ImageTextAlignmentAnalyzer.InferAlignment(
            [RectangleRegion("single", 10, 10, 80, 20)]);

        Assert.AreEqual(ImageTextAlignment.Center, alignment);
    }

    [TestMethod]
    public void InferAlignment_DefaultsIsolatedBodyTextToLeft()
    {
        var alignment = ImageTextAlignmentAnalyzer.InferAlignment(
        [
            RectangleRegion(
                "This place is cheaper than Tim Ho Wan in Hong Kong",
                10,
                10,
                260,
                20)
        ]);

        Assert.AreEqual(ImageTextAlignment.Left, alignment);
    }

    [TestMethod]
    public void InferAlignment_PreservesLeftAlignedParagraphs()
    {
        var alignment = ImageTextAlignmentAnalyzer.InferAlignment(
        [
            RectangleRegion("first", 10, 0, 100, 20),
            RectangleRegion("second", 10, 24, 70, 20),
            RectangleRegion("third", 10, 48, 50, 20)
        ]);

        Assert.AreEqual(ImageTextAlignment.Left, alignment);
    }

    [TestMethod]
    public void InferAlignment_PreservesCenteredParagraphs()
    {
        var alignment = ImageTextAlignmentAnalyzer.InferAlignment(
        [
            RectangleRegion("first", 50, 0, 100, 20),
            RectangleRegion("second", 65, 24, 70, 20),
            RectangleRegion("third", 75, 48, 50, 20)
        ]);

        Assert.AreEqual(ImageTextAlignment.Center, alignment);
    }

    [TestMethod]
    public void InferAlignment_PreservesRightAlignedParagraphs()
    {
        var alignment = ImageTextAlignmentAnalyzer.InferAlignment(
        [
            RectangleRegion("first", 50, 0, 100, 20),
            RectangleRegion("second", 80, 24, 70, 20),
            RectangleRegion("third", 100, 48, 50, 20)
        ]);

        Assert.AreEqual(ImageTextAlignment.Right, alignment);
    }

    [TestMethod]
    public void InferAlignment_FallsBackToLeftWhenTheAnchorsAreAmbiguous()
    {
        var alignment = ImageTextAlignmentAnalyzer.InferAlignment(
        [
            RectangleRegion("first", 10, 0, 80, 20),
            RectangleRegion("second", 10, 24, 80, 20)
        ]);

        Assert.AreEqual(ImageTextAlignment.Left, alignment);
    }

    [TestMethod]
    public void InferAlignment_UsesTheTextAxisForRotatedCenteredParagraphs()
    {
        const double angle = 30;
        var alignment = ImageTextAlignmentAnalyzer.InferAlignment(
        [
            RotatedRegion("first", 50, 0, 100, 20, angle),
            RotatedRegion("second", 65, 24, 70, 20, angle),
            RotatedRegion("third", 75, 48, 50, 20, angle)
        ]);

        Assert.AreEqual(ImageTextAlignment.Center, alignment);
    }

    [TestMethod]
    public void Analyze_GroupsAdjacentStandaloneRegionsBeforeInferringAlignment()
    {
        var overlays = new[]
        {
            Overlay(RectangleRegion("first", 10, 0, 100, 20)),
            Overlay(RectangleRegion("second", 10, 24, 70, 20)),
            Overlay(RectangleRegion("third", 10, 48, 50, 20)),
            Overlay(RectangleRegion("isolated", 300, 0, 80, 20))
        };

        var alignments = ImageTextAlignmentAnalyzer.Analyze(overlays);

        Assert.AreEqual(ImageTextAlignment.Left, alignments[overlays[0]]);
        Assert.AreEqual(ImageTextAlignment.Left, alignments[overlays[1]]);
        Assert.AreEqual(ImageTextAlignment.Left, alignments[overlays[2]]);
        Assert.AreEqual(ImageTextAlignment.Center, alignments[overlays[3]]);
    }

    [TestMethod]
    public void Analyze_PreservesLeftAlignmentAcrossParagraphSpacingAndDifferentRegionHeights()
    {
        var overlays = new[]
        {
            Overlay(RectangleRegion(
                "The most affordable Dim Sum in the Los Angeles/OC area is inside the Hilton\nin San Gabriel. Only $4 per dish vs. $7+ elsewhere.",
                10,
                0,
                300,
                44)),
            Overlay(RectangleRegion(
                "They still use traditional push carts, so you pick what you want\nas they come around.",
                10,
                66,
                240,
                44)),
            Overlay(RectangleRegion(
                "This place is cheaper than Tim Ho Wan in Hong Kong",
                10,
                132,
                220,
                20))
        };

        var alignments = ImageTextAlignmentAnalyzer.Analyze(overlays);

        Assert.IsTrue(overlays.All(overlay =>
            alignments[overlay] == ImageTextAlignment.Left));
    }

    [TestMethod]
    public void CircularMeanAngle_RemainsNearTheHalfTurnAcrossTheAngleBoundary()
    {
        var angle = ImageTextAlignmentAnalyzer.CircularMeanAngle([179, -179]);

        Assert.AreEqual(180, Math.Abs(angle), 0.001);
    }

    private static ImageTranslationOverlay Overlay(OcrTextRegion region) =>
        new(region, "translated");

    private static OcrTextRegion RectangleRegion(
        string text,
        double x,
        double y,
        double width,
        double height) =>
        new(
            text,
            [
                new ImagePoint(x, y),
                new ImagePoint(x + width, y),
                new ImagePoint(x + width, y + height),
                new ImagePoint(x, y + height)
            ],
            0);

    private static OcrTextRegion RotatedRegion(
        string text,
        double left,
        double top,
        double width,
        double height,
        double angle)
    {
        var radians = angle * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        ImagePoint Point(double horizontal, double vertical) =>
            new(
                horizontal * cosine - vertical * sine,
                horizontal * sine + vertical * cosine);

        return new OcrTextRegion(
            text,
            [
                Point(left, top),
                Point(left + width, top),
                Point(left + width, top + height),
                Point(left, top + height)
            ],
            angle);
    }
}
