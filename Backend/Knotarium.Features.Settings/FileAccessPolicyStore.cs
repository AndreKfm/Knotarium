using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Settings;

/// <summary>Wire/storage shape of one path grant. <see cref="Mode"/> is "read", "write", or "both".</summary>
public sealed record FileAccessRuleDto(string Path, string Mode);

/// <summary>Wire/storage shape of the file-access policy (persisted as a JSON <see cref="AppSetting"/>).</summary>
public sealed record FileAccessPolicyDto(
    bool TotalAccess,
    List<FileAccessRuleDto> Rules,
    long? MinFreeBytes,
    double? MinFreePercent)
{
    public static FileAccessPolicyDto Empty { get; } = new(false, new List<FileAccessRuleDto>(), null, null);
}

/// <summary>
/// Reads/writes the instance-global file-access policy and supplies it to the file-node guard.
/// Persisted as a single JSON <see cref="AppSetting"/> under <see cref="AppSettingKeys.FileAccessPolicy"/>;
/// an unset or unparseable value is treated as deny-by-default (secure fallback). Implements the Core
/// <see cref="IFileAccessPolicyProvider"/> seam so the guard depends only on Core.
/// </summary>
public sealed class FileAccessPolicyStore : IFileAccessPolicyProvider
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly GlobalSettingsService _settings;

    public FileAccessPolicyStore(GlobalSettingsService settings) => _settings = settings;

    /// <summary>The stored policy in wire form (for the settings API). Never null — defaults to <see cref="FileAccessPolicyDto.Empty"/>.</summary>
    public async Task<FileAccessPolicyDto> GetDtoAsync(CancellationToken cancellationToken = default)
    {
        var json = await _settings.GetAsync(AppSettingKeys.FileAccessPolicy, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return FileAccessPolicyDto.Empty;
        }
        try
        {
            return JsonSerializer.Deserialize<FileAccessPolicyDto>(json, Json) ?? FileAccessPolicyDto.Empty;
        }
        catch (JsonException)
        {
            return FileAccessPolicyDto.Empty;
        }
    }

    /// <summary>Persist a new policy. The input is normalized (blank paths dropped, reserves clamped) first.</summary>
    public Task SetDtoAsync(FileAccessPolicyDto dto, CancellationToken cancellationToken = default)
    {
        var clean = Normalize(dto);
        return _settings.SetAsync(AppSettingKeys.FileAccessPolicy, JsonSerializer.Serialize(clean, Json), cancellationToken);
    }

    /// <summary>The stored policy mapped to the domain model the guard enforces.</summary>
    public async Task<FileAccessPolicy> GetPolicyAsync(CancellationToken cancellationToken = default)
        => ToDomain(await GetDtoAsync(cancellationToken));

    private static FileAccessPolicyDto Normalize(FileAccessPolicyDto dto)
    {
        var rules = (dto.Rules ?? new List<FileAccessRuleDto>())
            .Where(r => !string.IsNullOrWhiteSpace(r?.Path))
            .Select(r => new FileAccessRuleDto(r.Path.Trim(), NormalizeMode(r.Mode)))
            .ToList();

        long? minBytes = dto.MinFreeBytes is { } b && b > 0 ? b : null;
        double? minPct = dto.MinFreePercent is { } p && p > 0 ? Math.Min(p, 100.0) : null;

        return new FileAccessPolicyDto(dto.TotalAccess, rules, minBytes, minPct);
    }

    private static string NormalizeMode(string? mode) => (mode ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "write" => "write",
        "both" => "both",
        _ => "read",
    };

    private static FileAccessPolicy ToDomain(FileAccessPolicyDto dto)
    {
        var rules = (dto.Rules ?? new List<FileAccessRuleDto>())
            .Where(r => !string.IsNullOrWhiteSpace(r?.Path))
            .Select(r => new FileAccessRule(r.Path, ToMode(r.Mode)))
            .ToList();
        return new FileAccessPolicy(dto.TotalAccess, rules, dto.MinFreeBytes, dto.MinFreePercent);
    }

    private static FileAccessMode ToMode(string? mode) => (mode ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "write" => FileAccessMode.Write,
        "both" => FileAccessMode.ReadWrite,
        "read" => FileAccessMode.Read,
        _ => FileAccessMode.None,
    };
}
