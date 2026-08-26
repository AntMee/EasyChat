using Avalonia;
using Avalonia.Media;
using EasyChat.Presentation.ImageTranslation;

namespace EasyChat.Presentation.Tests.ImageTranslation;

[TestClass]
public sealed class AvaloniaImageTranslationRendererTests
{
    [TestMethod]
    public void CalculatePreferredFontSize_UsesTheOrientedBoxShortEdgeForRotatedText()
    {
        var bounds = new Rect(0, 0, 300, 32);

        var horizontal = AvaloniaImageTranslationRenderer.CalculatePreferredFontSize(bounds, 0);
        var rotated = AvaloniaImageTranslationRenderer.CalculatePreferredFontSize(bounds, 90);

        Assert.AreEqual(horizontal, rotated, 0.001);
        Assert.AreEqual(bounds.Height * 0.90, rotated, 0.001);
    }

    [TestMethod]
    public void CalculatePreferredFontSize_UsesOriginalLineHeightForMergedParagraphs()
    {
        var mergedBounds = new Rect(0, 0, 300, 120);

        var fontSize = AvaloniaImageTranslationRenderer.CalculatePreferredFontSize(
            mergedBounds,
            0,
            sourceLineCount: 3);

        Assert.AreEqual(mergedBounds.Height / 3 * 0.90, fontSize, 0.001);
    }

    [TestMethod]
    public void CalculatePreferredFontSize_MatchesTheTargetInkHeightToTheSourceLine()
    {
        var fontSize = AvaloniaImageTranslationRenderer.CalculatePreferredFontSize(
            sourceLineHeight: 32,
            targetInkHeight: 90,
            measurementFontSize: 100);

        Assert.AreEqual(32, fontSize, 0.001);
        Assert.IsGreaterThan(32 * 0.72, fontSize);
    }

    [TestMethod]
    public void CalculatePreferredFontSize_CapsScalingForLowInkPunctuation()
    {
        var fontSize = AvaloniaImageTranslationRenderer.CalculatePreferredFontSize(
            sourceLineHeight: 32,
            targetInkHeight: 10,
            measurementFontSize: 100);

        Assert.AreEqual(48, fontSize, 0.001);
    }

    [TestMethod]
    public void CalculateMinimumFittedFontSize_PreservesTheReadableShrinkFloor()
    {
        Assert.AreEqual(14, AvaloniaImageTranslationRenderer.CalculateMinimumFittedFontSize(20), 0.001);
        Assert.AreEqual(8, AvaloniaImageTranslationRenderer.CalculateMinimumFittedFontSize(10), 0.001);
        Assert.AreEqual(6, AvaloniaImageTranslationRenderer.CalculateMinimumFittedFontSize(6), 0.001);
    }

    [TestMethod]
    public void CalculateLineOriginX_AlignsTheVisibleInkInsteadOfTheTextAdvanceBox()
    {
        var inkBounds = new Rect(5, 4, 20, 10);

        var left = AvaloniaImageTranslationRenderer.CalculateLineOriginX(
            ImageTextAlignment.Left,
            100,
            inkBounds);
        var center = AvaloniaImageTranslationRenderer.CalculateLineOriginX(
            ImageTextAlignment.Center,
            100,
            inkBounds);
        var right = AvaloniaImageTranslationRenderer.CalculateLineOriginX(
            ImageTextAlignment.Right,
            100,
            inkBounds);

        Assert.AreEqual(-50, left + inkBounds.Left, 0.001);
        Assert.AreEqual(0, center + (inkBounds.Left + inkBounds.Right) / 2, 0.001);
        Assert.AreEqual(50, right + inkBounds.Right, 0.001);
    }

    [TestMethod]
    public void CalculateVerticalOrigin_CentersTheVisibleInkAroundTheOcrRegion()
    {
        var inkBounds = new Rect(0, 7, 20, 12);

        var origin = AvaloniaImageTranslationRenderer.CalculateVerticalOrigin(inkBounds);

        Assert.AreEqual(0, origin + (inkBounds.Top + inkBounds.Bottom) / 2, 0.001);
    }

    [TestMethod]
    public void CreateTypeface_PreservesTheSelectedWeightForMeasurementAndDrawing()
    {
        var typeface = AvaloniaImageTranslationRenderer.CreateTypeface(FontWeight.Bold);

        Assert.AreEqual(FontWeight.Bold, typeface.Weight);
    }

    [TestMethod]
    public void SelectContrastingForeground_UsesBlackForAnUnreadableLightForegroundOnLightBackground()
    {
        var selected = AvaloniaImageTranslationRenderer.SelectContrastingForeground(
            Colors.White,
            Color.FromRgb(240, 240, 240));

        Assert.AreEqual(Colors.Black, selected);
    }

    [TestMethod]
    public void SelectContrastingForeground_UsesWhiteForAnUnreadableDarkForegroundOnDarkBackground()
    {
        var selected = AvaloniaImageTranslationRenderer.SelectContrastingForeground(
            Colors.Black,
            Color.FromRgb(18, 18, 18));

        Assert.AreEqual(Colors.White, selected);
    }

    [TestMethod]
    public void SelectContrastingForeground_PreservesAnAlreadyLegibleOriginalColour()
    {
        var selected = AvaloniaImageTranslationRenderer.SelectContrastingForeground(
            Color.FromRgb(40, 100, 220),
            Colors.White);

        Assert.AreEqual(Color.FromRgb(40, 100, 220), selected);
    }

    [TestMethod]
    public void SelectForegroundColor_SeparatesDarkTextFromAWhiteBackground()
    {
        var pixels = Enumerable.Repeat(Colors.White, 20)
            .Append(Colors.Black)
            .Append(Colors.Black)
            .Append(Colors.Black)
            .Append(Colors.Black)
            .Append(Colors.Black)
            .ToArray();

        var selected = AvaloniaImageTranslationRenderer.SelectForegroundColor(Colors.White, pixels);

        Assert.AreEqual(Colors.Black, selected);
    }

    [TestMethod]
    public void SelectForegroundColor_UsesAHighContrastColourWhenNoTextColourCanBeInferred()
    {
        var selected = AvaloniaImageTranslationRenderer.SelectForegroundColor(
            Colors.White,
            Enumerable.Repeat(Colors.White, 12).ToArray());

        Assert.AreEqual(Colors.Black, selected);
    }

    [TestMethod]
    public void SelectForegroundColor_PreservesSubtleTextColourWhenItDiffersFromTheBackground()
    {
        var background = Color.FromRgb(230, 230, 230);
        var textColour = Color.FromRgb(205, 205, 220);
        var pixels = Enumerable.Repeat(background, 20)
            .Append(textColour)
            .Append(textColour)
            .Append(textColour)
            .Append(textColour)
            .Append(textColour)
            .ToArray();

        var selected = AvaloniaImageTranslationRenderer.SelectForegroundColor(background, pixels);

        Assert.AreEqual(textColour, selected);
    }

    [TestMethod]
    public void SelectContrastingForeground_PreservesSubtleButVisibleOriginalColour()
    {
        var original = Color.FromRgb(150, 150, 150);

        var selected = AvaloniaImageTranslationRenderer.SelectContrastingForeground(original, Colors.White);

        Assert.AreEqual(original, selected);
    }
}
