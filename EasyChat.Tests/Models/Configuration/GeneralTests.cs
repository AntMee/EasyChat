using EasyChat.Models.Configuration;
using Newtonsoft.Json;

namespace EasyChat.Tests.Models.Configuration;

[TestClass]
public class GeneralTests
{
    [TestMethod]
    public void Deserialize_LegacyLanguage_MapsToDisplayLanguage()
    {
        var config = JsonConvert.DeserializeObject<General>("{\"Language\":\"Simplified Chinese\"}");

        Assert.IsNotNull(config);
        Assert.AreEqual("Simplified Chinese", config.DisplayLanguage);
    }
}
