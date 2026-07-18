// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;
using Knotarium.Features.Settings;

namespace Knotarium.Features.Nodes.Sandbox;

/// <summary>Wire/storage shape of the sandbox settings. Mirrors <see cref="SandboxOptions"/> plus the
/// runtime banned-API screening flag; <c>Mode</c> travels as a string for readable JSON.</summary>
public sealed record SandboxSettingsDto(
    string Mode,
    bool AnalyzeAtRuntime,
    int WorkerCount,
    int MemoryLimitMb,
    int CpuPercent,
    int MaxRunSeconds,
    int KillGraceSeconds,
    int RecycleAfterRuns,
    bool RestrictedToken,
    bool ProxyCredentials,
    int MaxHttpResponseMb);

/// <summary>
/// Reads/writes the operator's sandbox configuration and applies it to the <b>live</b>
/// <see cref="SandboxOptions"/> singleton (plus the static banned-API screening flag), so an
/// admin can flip the execution mode without a restart. Persisted as a JSON
/// <see cref="AppSetting"/> under <see cref="AppSettingKeys.SandboxSettings"/>; unset ⇒ the
/// <c>Security:Sandbox</c> configuration section (bound at startup) stays authoritative.
/// Note: <c>WorkerCount</c> only sizes a <i>new</i> worker pool — an already-running Process
/// pool keeps its size until restart; the per-worker limits apply to newly spawned workers.
/// </summary>
public sealed class SandboxSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly GlobalSettingsService _settings;
    private readonly SandboxOptions _live;

    public SandboxSettingsStore(GlobalSettingsService settings, SandboxOptions live)
    {
        _settings = settings;
        _live = live;
    }

    /// <summary>The effective settings: the persisted blob when present, otherwise the live
    /// (configuration-seeded) options — so GET always reflects what is actually enforced.</summary>
    public async Task<SandboxSettingsDto> GetDtoAsync(CancellationToken cancellationToken = default)
    {
        var json = await _settings.GetAsync(AppSettingKeys.SandboxSettings, cancellationToken);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                if (JsonSerializer.Deserialize<SandboxSettingsDto>(json, Json) is { } dto)
                {
                    return Sanitize(dto);
                }
            }
            catch (JsonException)
            {
                // fall through to the live snapshot — never fail a settings read on a bad blob
            }
        }
        return FromLive();
    }

    public async Task<SandboxSettingsDto> SetDtoAsync(SandboxSettingsDto dto, CancellationToken cancellationToken = default)
    {
        var clean = Sanitize(dto);
        await _settings.SetAsync(AppSettingKeys.SandboxSettings, JsonSerializer.Serialize(clean, Json), cancellationToken);
        ApplyToLive(clean);
        return clean;
    }

    /// <summary>Startup restore: overlays a persisted operator choice onto the configuration-bound
    /// options. No persisted blob ⇒ no-op (appsettings stay authoritative).</summary>
    public async Task<bool> ApplyPersistedAsync(CancellationToken cancellationToken = default)
    {
        var json = await _settings.GetAsync(AppSettingKeys.SandboxSettings, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }
        try
        {
            if (JsonSerializer.Deserialize<SandboxSettingsDto>(json, Json) is { } dto)
            {
                ApplyToLive(Sanitize(dto));
                return true;
            }
        }
        catch (JsonException)
        {
            // ignore a corrupt blob; secure configuration defaults remain in force
        }
        return false;
    }

    private SandboxSettingsDto FromLive() => new(
        _live.Mode.ToString(),
        CSharpScriptCompiler.EnforceBannedApiAnalysis,
        _live.WorkerCount,
        _live.MemoryLimitMb,
        _live.CpuPercent,
        _live.MaxRunSeconds,
        _live.KillGraceSeconds,
        _live.RecycleAfterRuns,
        _live.RestrictedToken,
        _live.ProxyCredentials,
        _live.MaxHttpResponseMb);

    private void ApplyToLive(SandboxSettingsDto dto)
    {
        _live.Mode = ParseMode(dto.Mode);
        _live.WorkerCount = dto.WorkerCount;
        _live.MemoryLimitMb = dto.MemoryLimitMb;
        _live.CpuPercent = dto.CpuPercent;
        _live.MaxRunSeconds = dto.MaxRunSeconds;
        _live.KillGraceSeconds = dto.KillGraceSeconds;
        _live.RecycleAfterRuns = dto.RecycleAfterRuns;
        _live.RestrictedToken = dto.RestrictedToken;
        _live.ProxyCredentials = dto.ProxyCredentials;
        _live.MaxHttpResponseMb = dto.MaxHttpResponseMb;
        _live.Clamp();
        CSharpScriptCompiler.EnforceBannedApiAnalysis = dto.AnalyzeAtRuntime;
    }

    /// <summary>Normalizes the mode string and clamps every numeric to its supported range, using a
    /// scratch <see cref="SandboxOptions"/> so the shared clamp logic stays the single source of truth.</summary>
    private static SandboxSettingsDto Sanitize(SandboxSettingsDto dto)
    {
        var scratch = new SandboxOptions
        {
            Mode = ParseMode(dto.Mode),
            WorkerCount = dto.WorkerCount,
            MemoryLimitMb = dto.MemoryLimitMb,
            CpuPercent = dto.CpuPercent,
            MaxRunSeconds = dto.MaxRunSeconds,
            KillGraceSeconds = dto.KillGraceSeconds,
            RecycleAfterRuns = dto.RecycleAfterRuns,
            RestrictedToken = dto.RestrictedToken,
            ProxyCredentials = dto.ProxyCredentials,
            MaxHttpResponseMb = dto.MaxHttpResponseMb
        };
        scratch.Clamp();
        return new SandboxSettingsDto(
            scratch.Mode.ToString(), dto.AnalyzeAtRuntime, scratch.WorkerCount, scratch.MemoryLimitMb,
            scratch.CpuPercent, scratch.MaxRunSeconds, scratch.KillGraceSeconds, scratch.RecycleAfterRuns,
            scratch.RestrictedToken, scratch.ProxyCredentials, scratch.MaxHttpResponseMb);
    }

    private static SandboxMode ParseMode(string? mode)
        => Enum.TryParse<SandboxMode>(mode, ignoreCase: true, out var parsed) ? parsed : SandboxMode.InProcess;
}
