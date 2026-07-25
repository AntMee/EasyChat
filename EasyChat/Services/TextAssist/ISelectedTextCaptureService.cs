using System.Threading;
using System.Threading.Tasks;

namespace EasyChat.Services.TextAssist;

public interface ISelectedTextCaptureService
{
    Task<SelectedTextSnapshot?> CaptureAsync(CancellationToken cancellationToken = default);
}

public sealed record SelectedTextSnapshot(string Text, int X, int Y);
