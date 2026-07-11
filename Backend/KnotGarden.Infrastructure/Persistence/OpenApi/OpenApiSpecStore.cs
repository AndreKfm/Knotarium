using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts.OpenApi;
using KnotGarden.Core.Domain;
using KnotGarden.Core.Domain.OpenApi;
using Microsoft.EntityFrameworkCore;

namespace KnotGarden.Infrastructure.Persistence.OpenApi;

public sealed class OpenApiSpecStore : IOpenApiSpecStore
{
    private readonly AppDbContext _db;

    public OpenApiSpecStore(AppDbContext db) => _db = db;

    public async Task<ImportedSpec> SaveAsync(ParsedSpec spec, CancellationToken ct = default)
    {
        var specId = spec.Metadata.Id.Value;

        var existing = await _db.OpenApiSpecs
            .Include(s => s.Versions)
            .FirstOrDefaultAsync(s => s.Id == specId, ct);

        int nextVersion;
        if (existing is null)
        {
            existing = new OpenApiSpecEntity
            {
                Id = specId,
                Title = spec.Metadata.Title,
                ApiVersion = spec.Metadata.Version,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.OpenApiSpecs.Add(existing);
            nextVersion = 1;
        }
        else
        {
            existing.Title = spec.Metadata.Title;
            existing.ApiVersion = spec.Metadata.Version;
            nextVersion = existing.Versions.Count > 0
                ? existing.Versions.Max(v => v.VersionNumber) + 1
                : 1;
        }

        var parsedJson = JsonSerializer.Serialize(spec, PersistenceJsonOptions.Default);

        var version = new OpenApiSpecVersionEntity
        {
            RowId = Guid.NewGuid(),
            SpecId = specId,
            VersionNumber = nextVersion,
            OriginalFormat = spec.Metadata.OriginalFormat,
            ParsedSpecJson = parsedJson,
            ImportedAtUtc = DateTimeOffset.UtcNow
        };
        _db.OpenApiSpecVersions.Add(version);
        await _db.SaveChangesAsync(ct);

        return spec.Metadata with { SpecVersionNumber = nextVersion, ImportedAtUtc = version.ImportedAtUtc };
    }

    public async Task<(ImportedSpec Spec, ParsedSpec Full)?> GetLatestAsync(OpenApiSpecId id, CancellationToken ct = default)
    {
        var version = await _db.OpenApiSpecVersions
            .Where(v => v.SpecId == id.Value)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(ct);

        if (version is null) return null;
        return Deserialize(version);
    }

    public async Task<(ImportedSpec Spec, ParsedSpec Full)?> GetVersionAsync(OpenApiSpecId id, int versionNumber, CancellationToken ct = default)
    {
        var version = await _db.OpenApiSpecVersions
            .FirstOrDefaultAsync(v => v.SpecId == id.Value && v.VersionNumber == versionNumber, ct);

        if (version is null) return null;
        return Deserialize(version);
    }

    public async Task<IReadOnlyList<ImportedSpec>> ListAsync(CancellationToken ct = default)
    {
        // Return only the latest version per spec
        var latest = await _db.OpenApiSpecVersions
            .GroupBy(v => v.SpecId)
            .Select(g => g.OrderByDescending(v => v.VersionNumber).First())
            .ToListAsync(ct);

        return latest.Select(v => Deserialize(v).Spec).ToList();
    }

    public async Task<IReadOnlyList<ImportedSpec>> GetVersionsAsync(OpenApiSpecId id, CancellationToken ct = default)
    {
        var versions = await _db.OpenApiSpecVersions
            .Where(v => v.SpecId == id.Value)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync(ct);

        return versions.Select(v => Deserialize(v).Spec).ToList();
    }

    public async Task<ApiOperation?> GetOperationAsync(OpenApiSpecId id, string operationId, CancellationToken ct = default)
    {
        var result = await GetLatestAsync(id, ct);
        if (result is null) return null;
        return result.Value.Full.Operations.FirstOrDefault(o => o.OperationId == operationId);
    }

    public async Task<bool> DeleteAsync(OpenApiSpecId id, CancellationToken ct = default)
    {
        var spec = await _db.OpenApiSpecs
            .Include(s => s.Versions)
            .FirstOrDefaultAsync(s => s.Id == id.Value, ct);

        if (spec is null) return false;

        _db.OpenApiSpecVersions.RemoveRange(spec.Versions);
        _db.OpenApiSpecs.Remove(spec);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static (ImportedSpec Spec, ParsedSpec Full) Deserialize(OpenApiSpecVersionEntity entity)
    {
        var full = JsonSerializer.Deserialize<ParsedSpec>(entity.ParsedSpecJson, PersistenceJsonOptions.Default)!;
        var spec = full.Metadata with { SpecVersionNumber = entity.VersionNumber, ImportedAtUtc = entity.ImportedAtUtc };
        return (spec, full with { Metadata = spec });
    }
}
