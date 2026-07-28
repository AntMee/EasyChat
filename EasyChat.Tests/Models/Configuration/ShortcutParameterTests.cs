using EasyChat.Models.Configuration;
using Newtonsoft.Json;

namespace EasyChat.Tests.Models.Configuration;

[TestClass]
public class ShortcutParameterTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ReadSelectedText_RoundTrips(bool expected)
    {
        var json = JsonConvert.SerializeObject(new ShortcutParameter { ReadSelectedText = expected });
        var parameter = JsonConvert.DeserializeObject<ShortcutParameter>(json);

        Assert.IsNotNull(parameter);
        Assert.AreEqual(expected, parameter.ReadSelectedText);
    }

    [TestMethod]
    public void LegacyConfiguration_LeavesReadSelectedTextUnspecified()
    {
        var parameter = JsonConvert.DeserializeObject<ShortcutParameter>("{\"Value\":\"\"}");

        Assert.IsNotNull(parameter);
        Assert.IsNull(parameter.ReadSelectedText);
    }

    [TestMethod]
    public void InputTranslateSimulatedKeys_RoundTrip()
    {
        var expected = new ShortcutParameter
        {
            InputTranslateBeforeKey = "Enter",
            InputTranslateAfterKey = "Ctrl + Enter"
        };

        var json = JsonConvert.SerializeObject(expected);
        var parameter = JsonConvert.DeserializeObject<ShortcutParameter>(json);

        Assert.IsNotNull(parameter);
        Assert.AreEqual(expected.InputTranslateBeforeKey, parameter.InputTranslateBeforeKey);
        Assert.AreEqual(expected.InputTranslateAfterKey, parameter.InputTranslateAfterKey);
    }

    [TestMethod]
    public void LegacyConfiguration_LeavesInputTranslateSimulatedKeysUnspecified()
    {
        var parameter = JsonConvert.DeserializeObject<ShortcutParameter>("{\"Value\":\"\"}");

        Assert.IsNotNull(parameter);
        Assert.IsNull(parameter.InputTranslateBeforeKey);
        Assert.IsNull(parameter.InputTranslateAfterKey);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ReplaceCurrentInput_RoundTrips(bool expected)
    {
        var json = JsonConvert.SerializeObject(new ShortcutParameter { ReplaceCurrentInput = expected });
        var parameter = JsonConvert.DeserializeObject<ShortcutParameter>(json);

        Assert.IsNotNull(parameter);
        Assert.AreEqual(expected, parameter.ReplaceCurrentInput);
    }

    [TestMethod]
    public void LegacyConfiguration_LeavesReplaceCurrentInputUnspecified()
    {
        var parameter = JsonConvert.DeserializeObject<ShortcutParameter>("{\"Value\":\"\"}");

        Assert.IsNotNull(parameter);
        Assert.IsNull(parameter.ReplaceCurrentInput);
    }
}
