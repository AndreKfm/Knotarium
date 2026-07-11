using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Ai.Providers;
using KnotGarden.Features.Settings;

namespace KnotGarden.Features.Ai;

/// <summary>Reads/writes the single active <see cref="AiProviderConfig"/> (persisted as a JSON AppSetting).</summary>
public interface IAiProviderConfigStore
{
    Task<AiProviderConfig?> GetAsync(CancellationToken cancellationToken = default);
    Task SetAsync(AiProviderConfig config, CancellationToken cancellationToken = default);
}

public sealed class AiProviderConfigStore : IAiProviderConfigStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly GlobalSettingsService _settings;

    public AiProviderConfigStore(GlobalSettingsService settings) => _settings = settings;

    public async Task<AiProviderConfig?> GetAsync(CancellationToken cancellationToken = default)
    {
        var json = await _settings.GetAsync(AppSettingKeys.AiProviderConfig, cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<AiProviderConfig>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SetAsync(AiProviderConfig config, CancellationToken cancellationToken = default) =>
        _settings.SetAsync(AppSettingKeys.AiProviderConfig, JsonSerializer.Serialize(config, Json), cancellationToken);
}
