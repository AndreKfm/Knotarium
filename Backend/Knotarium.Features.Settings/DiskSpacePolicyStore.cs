// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Settings;

/// <summary>
/// Wire/storage shape of the disk-space guard policy. <see cref="MinFreeSpaceMb"/> is the free-space
/// floor on the data volume below which the runtime is disarmed (stopping new runs); <c>0</c> disables
/// the guard. <see cref="FreeSpaceCheckSeconds"/> is how often to check (floored at 30s). Mirrors the
/// "Storage" configuration keys so the two are interchangeable.
/// </summary>
public sealed record DiskSpacePolicyDto(
    int MinFreeSpaceMb,
    int FreeSpaceCheckSeconds);

/// <summary>
/// Configuration-seeded fallback applied when no disk-space policy has been persisted yet. Bound from the
/// host's "Storage" section and injected into <see cref="DiskSpacePolicyStore"/>; a plain record so this
/// slice needn't reference <c>IConfiguration</c>. Defaults match the guard's historical behavior.
/// </summary>
public sealed record DiskSpaceDefaults(
    int MinFreeSpaceMb = 256,
    int FreeSpaceCheckSeconds = 60);

/// <summary>
/// Reads/writes the instance-global disk-space guard policy. Persisted as a JSON <see cref="AppSetting"/>
/// under <see cref="AppSettingKeys.DiskSpacePolicy"/>; unset or unparseable ⇒ the configuration-seeded
/// defaults, so GET always reflects what the guard actually enforces. The DiskSpaceGuardWorker (in the
/// host) re-reads this store on every check, so an admin edit applies without a restart.
/// </summary>
public sealed class DiskSpacePolicyStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // The check floors at 30s (matching the worker's Math.Max(30, …)). Sane ceilings so a typo can't
    // schedule an absurd interval or an unreachable free-space floor (10 TB in MB).
    private const int MinCheckSeconds = 30;
    private const int MaxCheckSeconds = 86_400; // one day
    private const int MaxFreeMb = 10_000_000;

    private readonly GlobalSettingsService _settings;
    private readonly DiskSpaceDefaults _defaults;

    public DiskSpacePolicyStore(GlobalSettingsService settings, DiskSpaceDefaults defaults)
    {
        _settings = settings;
        _defaults = defaults;
    }

    /// <summary>The effective policy: the persisted blob when present, otherwise the configuration-seeded
    /// defaults — so GET always reflects what the disk-space guard actually enforces.</summary>
    public async Task<DiskSpacePolicyDto> GetDtoAsync(CancellationToken cancellationToken = default)
    {
        var json = await _settings.GetAsync(AppSettingKeys.DiskSpacePolicy, cancellationToken);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                if (JsonSerializer.Deserialize<DiskSpacePolicyDto>(json, Json) is { } dto)
                {
                    return Normalize(dto);
                }
            }
            catch (JsonException)
            {
                // fall through to the configuration-seeded defaults — never fail a read on a bad blob
            }
        }
        return Normalize(new DiskSpacePolicyDto(_defaults.MinFreeSpaceMb, _defaults.FreeSpaceCheckSeconds));
    }

    public async Task<DiskSpacePolicyDto> SetDtoAsync(DiskSpacePolicyDto dto, CancellationToken cancellationToken = default)
    {
        var clean = Normalize(dto);
        await _settings.SetAsync(AppSettingKeys.DiskSpacePolicy, JsonSerializer.Serialize(clean, Json), cancellationToken);
        return clean;
    }

    // MinFreeSpaceMb floors at 0 (= guard disabled); the check interval floors at 30 seconds.
    private static DiskSpacePolicyDto Normalize(DiskSpacePolicyDto dto) => new(
        Math.Clamp(dto.MinFreeSpaceMb, 0, MaxFreeMb),
        Math.Clamp(dto.FreeSpaceCheckSeconds, MinCheckSeconds, MaxCheckSeconds));
}
