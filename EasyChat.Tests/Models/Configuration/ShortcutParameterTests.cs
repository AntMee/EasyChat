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
}
