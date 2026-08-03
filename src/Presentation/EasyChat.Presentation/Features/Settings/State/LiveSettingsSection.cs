using System.Runtime.CompilerServices;
using EasyChat.Contracts.Settings;
using ReactiveUI;

namespace EasyChat.Presentation.Features.Settings.State;

public abstract class LiveSettingsSection(
    SettingsSection section,
    Func<SettingsSection, EasyChat.Shared.Results.Result> commit) : ReactiveObject
{
    private readonly Func<SettingsSection, EasyChat.Shared.Results.Result> _commit = commit;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        this.RaiseAndSetIfChanged(ref field, value, propertyName);
        _commit(section);
        return true;
    }

    protected void Commit() => _commit(section);
}
