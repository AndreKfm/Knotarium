// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Security;
using Xunit;

namespace Knotarium.Tests.Security;

public class CorrelationTokenTests : IDisposable
{
    private readonly SqliteConnection _sharedConnection;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;
    private readonly ICorrelationTokenCrypto _crypto = new CorrelationTokenCrypto();

    public CorrelationTokenTests()
    {
        // Setup a shared SQLite in-memory connection for concurrency and integration testing
        _sharedConnection = new SqliteConnection("DataSource=:memory:");
        _sharedConnection.Open();

        _dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_sharedConnection)
            .Options;

        // Initialize the shared database schema once
        using var context = new AppDbContext(_dbContextOptions);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sharedConnection.Dispose();
    }

    private class MockTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void SetUtcNow(DateTimeOffset time)
        {
            _now = time;
        }
    }

    [Fact]
    public void GenerateRawToken_ProducesHighEntropyBase64UrlSafeTokens()
    {
        // Act
        var rawToken = _crypto.GenerateRawToken();

        // Assert - Base64Url safety checks (no +, /, or = characters)
        Assert.False(string.IsNullOrWhiteSpace(rawToken));
        Assert.DoesNotContain("+", rawToken);
        Assert.DoesNotContain("/", rawToken);
        Assert.DoesNotContain("=", rawToken);

        // Decodes successfully using built-in high-performance Base64Url helper to exactly 32 bytes
        var decodedBytes = System.Buffers.Text.Base64Url.DecodeFromChars(rawToken);
        Assert.Equal(32, decodedBytes.Length);

        // Sequence Uniqueness: 1,000 consecutive generated tokens must be entirely unique
        var tokenSet = new HashSet<string>();
        for (int i = 0; i < 1000; i++)
        {
            var token = _crypto.GenerateRawToken();
            Assert.True(tokenSet.Add(token), $"Duplicate token generated: {token}");
        }
    }

    [Fact]
    public void HashToken_IsDeterministicAndSecureHex()
    {
        var rawToken1 = _crypto.GenerateRawToken();
        var rawToken2 = _crypto.GenerateRawToken();

        // Act & Assert - Determinism
        var hash1A = _crypto.HashToken(rawToken1);
        var hash1B = _crypto.HashToken(rawToken1);
        var hash2 = _crypto.HashToken(rawToken2);

        Assert.Equal(hash1A, hash1B);
        Assert.NotEqual(hash1A, hash2);

        // Hashing Properties (64 hex characters, lowercase only)
        Assert.Equal(64, hash1A.Length);
        Assert.True(hash1A.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));
    }

    [Fact]
    public async Task CreateTokenAsync_ValidatesPositiveTTL()
    {
        // Arrange
        using var context = new AppDbContext(_dbContextOptions);
        var timeProvider = new MockTimeProvider();
        var service = new CorrelationTokenService(context, _crypto, timeProvider, NullLogger<CorrelationTokenService>.Instance);
        var execId = ExecutionInstanceId.New();
        var nodeId = NodeId.Create("webhook-1");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.CreateTokenAsync(execId, nodeId, TimeSpan.Zero));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.CreateTokenAsync(execId, nodeId, TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public async Task VerifyAndClaimAsync_ValidatesExpirationWithMockTime()
    {
        // Arrange
        using var context = new AppDbContext(_dbContextOptions);
        var timeProvider = new MockTimeProvider();
        var service = new CorrelationTokenService(context, _crypto, timeProvider, NullLogger<CorrelationTokenService>.Instance);
        
        var execId = ExecutionInstanceId.New();
        var nodeId = NodeId.Create("webhook-1");
        var ttl = TimeSpan.FromMinutes(10);

        // Act 1 - Create token
        var created = await service.CreateTokenAsync(execId, nodeId, ttl);
        Assert.NotNull(created.RawToken);

        // Advance mock time provider beyond expiration TTL (10 mins + 1 second)
        timeProvider.SetUtcNow(timeProvider.GetUtcNow().Add(ttl).AddSeconds(1));

        // Act 2 - Attempt to claim expired token
        var claimed = await service.VerifyAndClaimAsync(created.RawToken);

        // Assert - Expired token is rejected
        Assert.Null(claimed);
    }

    [Fact]
    public async Task VerifyAndClaimAsync_AtomicMultiContextConcurrency_OnlyOneSucceeds()
    {
        // Arrange
        var dbFile = $"CorrelationTokenTests_{Guid.NewGuid()}.db";
        var connectionString = $"Data Source={dbFile}";

        // Initialize schema on the temporary file
        using (var setupConnection = new SqliteConnection(connectionString))
        {
            setupConnection.Open();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(setupConnection).Options;
            using var setupContext = new AppDbContext(options);
            setupContext.Database.EnsureCreated();
        }

        var execId = ExecutionInstanceId.New();
        var nodeId = NodeId.Create("concurrency-node");
        var ttl = TimeSpan.FromMinutes(5);

        string rawToken;
        using (var setupConnection = new SqliteConnection(connectionString))
        {
            setupConnection.Open();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(setupConnection).Options;
            using var setupContext = new AppDbContext(options);
            var timeProvider = new MockTimeProvider();
            var service = new CorrelationTokenService(setupContext, _crypto, timeProvider, NullLogger<CorrelationTokenService>.Instance);
            var created = await service.CreateTokenAsync(execId, nodeId, ttl);
            rawToken = created.RawToken;
        }

        // Fire 10 concurrent claim tasks
        // Each task initializes its own connection and context to the same SQLite file
        int taskCount = 10;
        var tasks = new Task<CorrelationToken?>[taskCount];

        for (int i = 0; i < taskCount; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                using var testConnection = new SqliteConnection(connectionString);
                testConnection.Open();
                var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(testConnection).Options;
                using var testContext = new AppDbContext(options);
                var timeProvider = new MockTimeProvider();
                var service = new CorrelationTokenService(testContext, _crypto, timeProvider, NullLogger<CorrelationTokenService>.Instance);
                return await service.VerifyAndClaimAsync(rawToken);
            });
        }

        try
        {
            // Act
            var results = await Task.WhenAll(tasks);

            // Assert - Exactly 1 task successfully claimed the token, while 9 tasks failed (returned null)
            var successfulClaims = results.Where(r => r != null).ToList();
            var failedClaims = results.Where(r => r == null).ToList();

            Assert.Single(successfulClaims);
            Assert.Equal(9, failedClaims.Count);

            var claimedToken = successfulClaims[0];
            Assert.NotNull(claimedToken);
            Assert.NotNull(claimedToken.ConsumedAtUtc);
        }
        finally
        {
            // Cleanup database file
            SqliteConnection.ClearAllPools(); // Ensure SQLite releases lock on the file
            if (System.IO.File.Exists(dbFile))
            {
                try
                {
                    System.IO.File.Delete(dbFile);
                }
                catch
                {
                    // Ignore cleanup failure in test
                }
            }
        }
    }
}
