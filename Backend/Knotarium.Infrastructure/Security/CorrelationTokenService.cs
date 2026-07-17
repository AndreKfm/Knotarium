// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Infrastructure.Security;

public class CorrelationTokenService : ICorrelationTokenService
{
    private readonly AppDbContext _dbContext;
    private readonly ICorrelationTokenCrypto _crypto;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CorrelationTokenService> _logger;

    public CorrelationTokenService(
        AppDbContext dbContext,
        ICorrelationTokenCrypto crypto,
        TimeProvider timeProvider,
        ILogger<CorrelationTokenService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CreatedCorrelationToken> CreateTokenAsync(
        ExecutionInstanceId executionInstanceId, 
        NodeId nodeId, 
        TimeSpan ttl, 
        CancellationToken cancellationToken = default)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");
        }

        var rawToken = _crypto.GenerateRawToken();
        var hashedToken = _crypto.HashToken(rawToken);
        var now = _timeProvider.GetUtcNow();

        var token = new CorrelationToken
        {
            Id = Guid.NewGuid(),
            HashedToken = hashedToken,
            ExecutionInstanceId = executionInstanceId,
            NodeId = nodeId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(ttl),
            ConsumedAtUtc = null
        };

        await _dbContext.CorrelationTokens.AddAsync(token, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Created correlation token. TokenHashPrefix={HashPrefix}", 
            hashedToken[..8]);

        return new CreatedCorrelationToken(token.Id, rawToken, token.ExpiresAtUtc);
    }

    public async Task<CorrelationToken?> VerifyAndClaimAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null; // Prevent parsing empty tokens
        }

        var hashed = _crypto.HashToken(rawToken);
        var now = _timeProvider.GetUtcNow();

        // Atomic claim operation executing a single SQLite UPDATE statement
        var affected = await _dbContext.CorrelationTokens
            .Where(t => t.HashedToken == hashed && t.ConsumedAtUtc == null && t.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAtUtc, now), cancellationToken);

        // Security check: Assert exactly 1 row was successfully claimed
        if (affected != 1)
        {
            _logger.LogWarning(
                "Failed to claim correlation token atomically (affected rows: {Affected}). TokenHashPrefix={HashPrefix}", 
                affected, hashed[..8]);
            return null;
        }

        _logger.LogInformation(
            "Successfully claimed correlation token atomically. TokenHashPrefix={HashPrefix}", 
            hashed[..8]);

        return await _dbContext.CorrelationTokens
            .SingleAsync(t => t.HashedToken == hashed, cancellationToken);
    }
}
