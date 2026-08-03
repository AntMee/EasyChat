using EasyChat.Shared.Results;

namespace EasyChat.Contracts.Platform;

public interface IExternalUriLauncher
{
    Result Open(Uri uri);
}
