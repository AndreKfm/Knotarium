using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;

namespace KnotGarden.Features.Settings;

/// <summary>Wire/storage shape of the capability policy: the set of enabled privileged capability tags.</summary>
public sealed record CapabilityPolicyDto(List<string> EnabledCapabilities)
{
    public static CapabilityPolicyDto Empty { get; } = new(new List<string>());
}

/// <summary>
/// Reads/writes the instance-global capability policy and answers the node guard's "is X enabled?" checks.
/// Persisted as a JSON <see cref="AppSetting"/> under <see cref="AppSettingKeys.CapabilityPolicy"/>; unset or
/// unparseable ⇒ empty (everything off), the secure default. Only recognized switchable capabilities are
/// persisted, so stale/unknown tags can't accumulate. Implements the Core <see cref="ICapabilityPolicy"/> seam.
/// </summary>
public sealed class CapabilityPolicyStore : ICapabilityPolicy
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Capabilities the policy can toggle. Filesystem is deliberately excluded — it has its own
    /// finer-grained <see cref="FileAccessPolicy"/>, so it isn't governed by this coarse on/off switch.</summary>
    public static readonly IReadOnlyList<string> Switchable = new[]
    {
        NodeCapabilities.CodeExecution,
        NodeCapabilities.Database,
    };

    private readonly GlobalSettingsService _settings;

    public CapabilityPolicyStore(GlobalSettingsService settings) => _settings = settings;

    public async Task<CapabilityPolicyDto> GetDtoAsync(CancellationToken cancellationToken = default)
    {
        var json = await _settings.GetAsync(AppSettingKeys.CapabilityPolicy, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return CapabilityPolicyDto.Empty;
        }
        try
        {
            var dto = JsonSerializer.Deserialize<CapabilityPolicyDto>(json, Json);
            return dto is null ? CapabilityPolicyDto.Empty : new CapabilityPolicyDto(Sanitize(dto.EnabledCapabilities));
        }
        catch (JsonException)
        {
            return CapabilityPolicyDto.Empty;
        }
    }

    public Task SetDtoAsync(CapabilityPolicyDto dto, CancellationToken cancellationToken = default)
    {
        var clean = new CapabilityPolicyDto(Sanitize(dto?.EnabledCapabilities));
        return _settings.SetAsync(AppSettingKeys.CapabilityPolicy, JsonSerializer.Serialize(clean, Json), cancellationToken);
    }

    public async Task<bool> IsEnabledAsync(string capability, CancellationToken cancellationToken = default)
    {
        var dto = await GetDtoAsync(cancellationToken);
        return dto.EnabledCapabilities.Any(c => string.Equals(c, capability, System.StringComparison.OrdinalIgnoreCase));
    }

    // Keep only recognized, switchable capabilities (deduped) so unknown or filesystem tags never persist.
    private static List<string> Sanitize(IEnumerable<string>? caps) => (caps ?? Enumerable.Empty<string>())
        .Where(c => Switchable.Any(s => string.Equals(s, c, System.StringComparison.OrdinalIgnoreCase)))
        .Select(c => Switchable.First(s => string.Equals(s, c, System.StringComparison.OrdinalIgnoreCase)))
        .Distinct()
        .ToList();
}
