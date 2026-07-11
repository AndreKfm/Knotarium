using System;

namespace KnotGarden.Core.Domain;

/// <summary>
/// A login account. Password is stored only as a salted hash (never plaintext). <see cref="Role"/> is
/// reserved for the later RBAC step (Gap 3, step 3) and defaults to "admin"; it is persisted now so no
/// schema change is needed when roles are enforced, but access is not yet gated on it.
/// </summary>
public class UserAccount
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "admin";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
