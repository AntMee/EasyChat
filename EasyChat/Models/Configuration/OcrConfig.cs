using Newtonsoft.Json;
using ReactiveUI;

namespace EasyChat.Models.Configuration;

[JsonObject(MemberSerialization.OptIn)]
public class OcrConfig : ReactiveObject
{
    private bool _useProxy;

    [JsonProperty]
    public bool UseProxy
    {
        get => _useProxy;
        set => this.RaiseAndSetIfChanged(ref _useProxy, value);
    }
}
