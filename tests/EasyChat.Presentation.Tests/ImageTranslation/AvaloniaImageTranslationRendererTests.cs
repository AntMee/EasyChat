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
        Assert.AreEqual(bounds.Height * 0.72, rotated, 0.001);
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
