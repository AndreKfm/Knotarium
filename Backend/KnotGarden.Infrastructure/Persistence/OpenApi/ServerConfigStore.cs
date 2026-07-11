using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts.OpenApi;
using KnotGarden.Core.Domain;
using KnotGarden.Core.Domain.OpenApi;
using Microsoft.EntityFrameworkCore;

namespace KnotGarden.Infrastructure.Persistence.OpenApi;

public sealed class ServerConfigStore : IServerConfigStore
{
    private readonly AppDbContext _db;

    public ServerConfigStore(AppDbContext db) => _db = db;

    public async Task<ServerConfigInfo> CreateAsync(ServerConfigInfo config, CancellationToken ct = default)
    {
        var entity = ToEntity(config);
        _db.ServerConfigs.Add(entity);
        await _db.SaveChangesAsync(ct);
        return config;
    }

    public async Task<ServerConfigInfo> UpdateAsync(ServerConfigInfo config, CancellationToken ct = default)
    {
        var entity = await _db.ServerConfigs.FirstOrDefaultAsync(c => c.Id == config.Id, ct)
            ?? throw new InvalidOperationException($"ServerConfig '{config.Id}' not found.");

        entity.Name = config.Name;
        entity.BaseUrl = config.BaseUrl;
        entity.ServerVariablesJson = JsonSerializer.Serialize(config.ServerVariables, PersistenceJsonOptions.Default);
        entity.SecuritySchemeType = config.SecuritySchemeType;
        entity.CredentialRef = config.CredentialRef;
        entity.AllowInsecureCertificate = config.AllowInsecureCertificate;
        entity.UpdatedAt = config.UpdatedAt;

        await _db.SaveChangesAsync(ct);
        return config;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var entity = await _db.ServerConfigs.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is not null)
        {
            _db.ServerConfigs.Remove(entity);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<ServerConfigInfo?> GetAsync(string id, CancellationToken ct = default)
    {
        var entity = await _db.ServerConfigs.FirstOrDefaultAsync(c => c.Id == id, ct);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<IReadOnlyList<ServerConfigInfo>> ListAsync(CancellationToken ct = default)
    {
        var entities = await _db.ServerConfigs.ToListAsync(ct);
        return entities.ConvertAll(ToDomain);
    }

    private static ServerConfigEntity ToEntity(ServerConfigInfo c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        BaseUrl = c.BaseUrl,
        ServerVariablesJson = JsonSerializer.Serialize(c.ServerVariables, PersistenceJsonOptions.Default),
        SecuritySchemeType = c.SecuritySchemeType,
        CredentialRef = c.CredentialRef,
        AllowInsecureCertificate = c.AllowInsecureCertificate,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt
    };

    private static ServerConfigInfo ToDomain(ServerConfigEntity e)
    {
        var vars = JsonSerializer.Deserialize<Dictionary<string, string>>(e.ServerVariablesJson, PersistenceJsonOptions.Default)
            ?? new Dictionary<string, string>();
        return new ServerConfigInfo(e.Id, e.Name, e.BaseUrl, vars, e.SecuritySchemeType, e.CredentialRef, e.CreatedAt, e.UpdatedAt, e.AllowInsecureCertificate);
    }
}
