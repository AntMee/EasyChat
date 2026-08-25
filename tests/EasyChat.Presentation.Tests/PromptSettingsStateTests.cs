using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Shared.Results;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class PromptSettingsStateTests
{
    [TestMethod]
    public void SelectedPromptId_UpdatesDefaultPrompt()
    {
        var commits = 0;
        var settings = CreateSettings("first", firstDefault: true);
        var state = new LivePromptSettings(settings, _ =>
        {
            commits++;
            return Result.Success();
        });

        state.SelectedPromptId = "second";

        Assert.AreEqual(1, commits);
        Assert.IsFalse(state.Entries.Single(entry => entry.Id == "first").IsDefault);
        Assert.IsTrue(state.Entries.Single(entry => entry.Id == "second").IsDefault);
    }

    [TestMethod]
    public void ExistingSelection_NormalizesDefaultPrompt()
    {
        var state = new LivePromptSettings(
            CreateSettings("second", firstDefault: true),
            _ => Result.Success());

        Assert.IsFalse(state.Entries.Single(entry => entry.Id == "first").IsDefault);
        Assert.IsTrue(state.Entries.Single(entry => entry.Id == "second").IsDefault);
    }

    private static PromptSettings CreateSettings(string selectedPromptId, bool firstDefault) => new(
        selectedPromptId,
        [
            new PromptEntrySettings("first", "First", "First role", firstDefault),
            new PromptEntrySettings("second", "Second", "Second role", !firstDefault)
        ]);
}
