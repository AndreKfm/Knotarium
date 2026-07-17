// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Api.Services.Auth;

/// <summary>
/// User-account operations for the authentication layer: create, credential validation (salted-hash
/// verify via the framework <see cref="PasswordHasher{TUser}"/>), listing, deletion, and password
/// change. Usernames are normalized to lower-case so lookups are case-insensitive. Passwords are only
/// ever stored hashed. Role checks are deferred to the later RBAC step.
/// </summary>
public sealed class UserService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;
    private readonly IPasswordHasher<UserAccount> _hasher = new PasswordHasher<UserAccount>();

    public UserService(AppDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public Task<int> CountAsync(CancellationToken ct = default) => _db.Users.CountAsync(ct);

    public async Task<UserAccount> CreateAsync(string username, string password, string role = "admin", CancellationToken ct = default)
    {
        var normalized = Normalize(username);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Username is required.");
        }
        if (string.IsNullOrEmpty(password) || password.Length < 8)
        {
            throw new ArgumentException("Password must be at least 8 characters.");
        }
        var normalizedRole = NormalizeRole(role);
        if (await _db.Users.AnyAsync(u => u.Username == normalized, ct))
        {
            throw new InvalidOperationException("A user with that name already exists.");
        }

        var now = _time.GetUtcNow();
        var user = new UserAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            Username = normalized,
            Role = normalizedRole,
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = _hasher.HashPassword(user, password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>Returns the user when the password matches; null otherwise. Transparently upgrades a
    /// legacy hash when the framework signals a rehash is needed.</summary>
    public async Task<UserAccount?> ValidateCredentialsAsync(string username, string password, CancellationToken ct = default)
    {
        var normalized = Normalize(username);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == normalized, ct);
        if (user is null)
        {
            return null;
        }

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, password);
            user.UpdatedAt = _time.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }
        return user;
    }

    public async Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken ct = default)
        => await _db.Users.OrderBy(u => u.Username).ToListAsync(ct);

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return false;
        }
        // Never remove the last account — that would lock everyone out.
        if (await _db.Users.CountAsync(ct) <= 1)
        {
            throw new InvalidOperationException("Cannot delete the last remaining user.");
        }
        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ChangePasswordAsync(string id, string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 8)
        {
            throw new ArgumentException("Password must be at least 8 characters.");
        }
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return false;
        }
        user.PasswordHash = _hasher.HashPassword(user, newPassword);
        user.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static string Normalize(string username) => (username ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>The roles the system recognizes. "admin" holds every privileged mutation gate.</summary>
    public const string AdminRole = "admin";
    public const string UserRole = "user";

    /// <summary>
    /// Constrain the assigned role to a known value. A blank role keeps the historical "admin" default
    /// (used by first-run setup and the config seed, which intentionally create an admin); any other
    /// value must be one of the recognized roles, so a caller can't persist an arbitrary role string.
    /// </summary>
    private static string NormalizeRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return AdminRole;
        }
        var trimmed = role.Trim().ToLowerInvariant();
        return trimmed switch
        {
            AdminRole => AdminRole,
            UserRole => UserRole,
            _ => throw new ArgumentException($"Unknown role '{role}'. Allowed roles: {AdminRole}, {UserRole}.")
        };
    }
}
