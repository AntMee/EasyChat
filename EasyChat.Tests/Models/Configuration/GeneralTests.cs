using EasyChat.Models.Configuration;
using System.Globalization;
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

    [TestMethod]
    public void NewConfiguration_UsesSystemLanguageWhenDisplayLanguageIsUnset()
    {
        var config = new General();
        var expected = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
            ? "Simplified Chinese"
            : "English";

        Assert.AreEqual(expected, config.DisplayLanguage);
        Assert.IsFalse(JsonConvert.SerializeObject(config).Contains("DisplayLanguage", StringComparison.Ordinal));
    }
}
