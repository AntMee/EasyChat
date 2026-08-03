using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Shared.Results;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class ShortcutEntryStateTests
{
    [TestMethod]
    public void DisplayTitle_UsesRemarkWhenPresent()
    {
        var entry = CreateEntry("  Translate and send  ");

        Assert.IsTrue(entry.HasRemark);
        Assert.AreEqual("Translate and send", entry.DisplayTitle);
        Assert.AreEqual("Translate and send", entry.ToContract().Remark);
    }

    [TestMethod]
    public void DisplayTitle_FallsBackToLocalizedActionName()
    {
        var entry = CreateEntry(null);

        Assert.IsFalse(entry.HasRemark);
        Assert.AreEqual(entry.ActionDisplayText, entry.DisplayTitle);
    }

    private static ShortcutEntryState CreateEntry(string? remark) => new(
        new ShortcutEntrySettings("InputTranslate", null, "Ctrl + Enter", true, remark),
        _ => Result.Success());
}
