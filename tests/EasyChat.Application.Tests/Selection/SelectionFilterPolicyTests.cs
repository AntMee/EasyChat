using EasyChat.Application.Selection;
using EasyChat.Contracts.Settings;

namespace EasyChat.Application.Tests.Selection;

[TestClass]
public sealed class SelectionFilterPolicyTests
{
    private static readonly SelectionAppEntrySettings[] AppList =
    [
        new("chrome.exe"),
        new("notepad.exe")
    ];

    [TestMethod]
    public void Disabled_AlwaysAllows()
    {
        Assert.IsTrue(SelectionFilterPolicy.IsAllowed(SelectionFilterMode.Disabled, AppList, "chrome.exe"));
        Assert.IsTrue(SelectionFilterPolicy.IsAllowed(SelectionFilterMode.Disabled, AppList, null));
        Assert.IsTrue(SelectionFilterPolicy.IsAllowed(SelectionFilterMode.Disabled, [], null));
    }

    [TestMethod]
    public void Whitelist_AllowsOnlyListedAppsCaseInsensitively()
    {
        Assert.IsTrue(SelectionFilterPolicy.IsAllowed(SelectionFilterMode.Whitelist, AppList, "chrome.exe"));
        Assert.IsTrue(SelectionFilterPolicy.IsAllowed(SelectionFilterMode.Whitelist, AppList, "CHROME.EXE"));
        Assert.IsFalse(SelectionFilterPolicy.IsAllowed(SelectionFilterMode.Whitelist, AppList, "msedge.exe"));
        Assert.IsFalse(SelectionFilterPolicy.IsAllowed(SelectionFilterMode.Whitelist, AppList, null));
    }

    [TestMethod]
    public void Blacklist_BlocksOnlyListedApps()
    {
        Assert.IsFalse(SelectionFilterPolicy.IsAllowed(SelectionFilterMode.Blacklist, AppList, "notepad.exe"));
        Assert.IsFalse(SelectionFilterPolicy.IsAllowed(SelectionFilterMode.Blacklist, AppList, "NOTEPAD.EXE"));
        Assert.IsTrue(SelectionFilterPolicy.IsAllowed(SelectionFilterMode.Blacklist, AppList, "msedge.exe"));
        Assert.IsTrue(SelectionFilterPolicy.IsAllowed(SelectionFilterMode.Blacklist, AppList, null));
    }

    [TestMethod]
    public void EmptyList_BlocksEverythingInWhitelistModeButNothingInBlacklistMode()
    {
        Assert.IsFalse(SelectionFilterPolicy.IsAllowed(SelectionFilterMode.Whitelist, [], "chrome.exe"));
        Assert.IsTrue(SelectionFilterPolicy.IsAllowed(SelectionFilterMode.Blacklist, [], "chrome.exe"));
    }

    [TestMethod]
    public void NullList_IsTreatedAsEmpty()
    {
        Assert.IsFalse(SelectionFilterPolicy.IsAllowed(SelectionFilterMode.Whitelist, null, "chrome.exe"));
        Assert.IsTrue(SelectionFilterPolicy.IsAllowed(SelectionFilterMode.Blacklist, null, "chrome.exe"));
    }
}
