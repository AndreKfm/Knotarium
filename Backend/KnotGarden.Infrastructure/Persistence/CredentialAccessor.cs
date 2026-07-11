using System;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace KnotGarden.Infrastructure.Persistence;

public sealed class CredentialAccessor : ISecretResolver, ICredentialAccessor
{
    private readonly AppDbContext _dbContext;
    private readonly ICredentialCipher _cipher;

    public CredentialAccessor(AppDbContext dbContext, ICredentialCipher cipher)
    {
        _dbContext = dbContext;
        _cipher = cipher;
    }

    public Task<string?> ResolveAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        return GetSecretAsync(secretRef, cancellationToken);
    }

    public async Task<string?> GetSecretAsync(string credentialRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentialRef))
        {
            return null;
        }

        SecretValue secret;

        if (credentialRef.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var envVarName = credentialRef.Substring(4);
            secret = new SecretValue(Environment.GetEnvironmentVariable(envVarName));
        }
        else
        {
            var credential = await _dbContext.Credentials
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == credentialRef, cancellationToken);

            if (credential == null)
            {
                return null;
            }

            secret = new SecretValue(_cipher.Decrypt(credential.EncryptedValue));
        }

        return secret.HasValue ? secret.Reveal() : null;
    }
}