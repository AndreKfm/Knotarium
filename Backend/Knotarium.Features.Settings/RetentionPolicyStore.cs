// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Settings;

/// <summary>
/// Wire/storage shape of the data-retention policy. All values are day/count limits where
/// <c>0</c> means "keep forever / keep all" (except <see cref="SweepIntervalMinutes"/>, which is
/// always at least 1). Mirrors the "Retention" configuration section so the two are interchangeable.
/// </summary>
public sealed record RetentionPolicyDto(
    int RunHistoryDays,
    int SweepIntervalMinutes,
    int MaxWorkflowVersionsPerWorkflow,
    int MaxOpenApiSpecVersionsPerSpec,
    int AuditEntryDays);

/// <summary>
/// Configuration-seeded fallback applied when no retention policy has been persisted yet. Bound from the
/// host's "Retention" section and injected into <see cref="RetentionPolicyStore"/>; a plain record so this
/// slice needn't reference <c>IConfiguration</c>. The defaults match the worker's historical behavior.
/// </summary>
public sealed record RetentionDefaults(
    int RunHistoryDays = 30,
    int SweepIntervalMinutes = 60,
    int MaxWorkflowVersionsPerWorkflow = 0,
    int MaxOpenApiSpecVersionsPerSpec = 0,
    int AuditEntryDays = 0);

/// <summary>
/// Reads/writes the instance-global data-retention policy that bounds database growth (run history +
/// their journal/logs, schedule fires, workflow/OpenAPI version history, and the audit log). Persisted
/// as a JSON <see cref="AppSetting"/> under <see cref="AppSettingKeys.RetentionPolicy"/>; unset or
/// unparseable ⇒ the "Retention" configuration section (bound from appsettings/env) stays authoritative,
/// so GET always reflects what the sweep actually enforces. The <see cref="JournalRetentionWorker"/>
/// (in the host) re-reads this store on every sweep, so an admin edit applies without a restart.
/// </summary>
public sealed class RetentionPolicyStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Sane upper bounds so a typo can't overflow a TimeSpan or schedule an absurd sweep. Days ≈ 100 years.
    private const int MaxDays = 36_500;
    private const int MaxSweepMinutes = 10_080; // one week
    private const int MaxCount = 1_000_000;

    private readonly GlobalSettingsService _settings;
    private readonly RetentionDefaults _defaults;

    public RetentionPolicyStore(GlobalSettingsService settings, RetentionDefaults defaults)
    {
        _settings = settings;
        _defaults = defaults;
    }

    /// <summary>The effective policy: the persisted blob when present, otherwise the configuration-seeded
    /// defaults — so GET always reflects what the retention sweep actually enforces.</summary>
    public async Task<RetentionPolicyDto> GetDtoAsync(CancellationToken cancellationToken = default)
    {
        var json = await _settings.GetAsync(AppSettingKeys.RetentionPolicy, cancellationToken);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                if (JsonSerializer.Deserialize<RetentionPolicyDto>(json, Json) is { } dto)
                {
                    return Normalize(dto);
                }
            }
            catch (JsonException)
            {
                // fall through to the configuration-seeded defaults — never fail a read on a bad blob
            }
        }
        return FromDefaults();
    }

    public async Task<RetentionPolicyDto> SetDtoAsync(RetentionPolicyDto dto, CancellationToken cancellationToken = default)
    {
        var clean = Normalize(dto);
        await _settings.SetAsync(AppSettingKeys.RetentionPolicy, JsonSerializer.Serialize(clean, Json), cancellationToken);
        return clean;
    }

    private RetentionPolicyDto FromDefaults() => Normalize(new RetentionPolicyDto(
        _defaults.RunHistoryDays,
        _defaults.SweepIntervalMinutes,
        _defaults.MaxWorkflowVersionsPerWorkflow,
        _defaults.MaxOpenApiSpecVersionsPerSpec,
        _defaults.AuditEntryDays));

    // Clamp every value to its supported range. Day/count fields floor at 0 (= keep forever / keep all);
    // the sweep interval floors at 1 minute (matching the worker's Math.Max(1, …)).
    private static RetentionPolicyDto Normalize(RetentionPolicyDto dto) => new(
        Math.Clamp(dto.RunHistoryDays, 0, MaxDays),
        Math.Clamp(dto.SweepIntervalMinutes, 1, MaxSweepMinutes),
        Math.Clamp(dto.MaxWorkflowVersionsPerWorkflow, 0, MaxCount),
        Math.Clamp(dto.MaxOpenApiSpecVersionsPerSpec, 0, MaxCount),
        Math.Clamp(dto.AuditEntryDays, 0, MaxDays));
}
