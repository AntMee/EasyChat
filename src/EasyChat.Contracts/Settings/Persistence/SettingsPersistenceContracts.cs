using EasyChat.Shared.Results;
using EasyChat.Contracts.Settings;

namespace EasyChat.Contracts.Settings.Persistence;

public interface ISettingsPersistenceGateway
{
    ValueTask<Result<SettingsBundle>> ReadAllAsync(
        CancellationToken cancellationToken = default);

    ValueTask<Result> WriteAllAsync(
        SettingsBundle settings,
        CancellationToken cancellationToken = default);

    ValueTask<Result> WriteAsync(
        SettingsSection section,
        SettingsBundle settings,
        CancellationToken cancellationToken = default);
}
