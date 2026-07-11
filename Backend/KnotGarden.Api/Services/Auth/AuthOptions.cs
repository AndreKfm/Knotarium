using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace KnotGarden.Api.Services.Auth;

/// <summary>
/// Resolved authentication configuration, exposed to endpoints that need to reason about it at request time
/// (e.g. admin-only gating). <see cref="Enabled"/> mirrors the <c>Auth:Enabled</c> flag read at startup.
/// </summary>
public sealed record AuthOptions(bool Enabled)
{
    /// <summary>The role name granted to the bootstrap/admin user (see UserService).</summary>
    public const string AdminRole = "admin";

    /// <summary>
    /// Admin gate for privileged mutations. When auth is enabled the caller must hold the admin role;
    /// when auth is disabled (single-operator / no-auth mode) it is a no-op, consistent with every other
    /// endpoint being anonymous in that mode. Returns a 403 result to short-circuit with, or null to proceed.
    /// </summary>
    public IResult? RequireAdmin(ClaimsPrincipal user) =>
        !Enabled || user.IsInRole(AdminRole)
            ? null
            : Results.StatusCode(StatusCodes.Status403Forbidden);
}
