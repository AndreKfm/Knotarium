using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using KnotGarden.Api.Services.Auth;
using KnotGarden.Core.Domain;

namespace KnotGarden.Api;

/// <summary>
/// Authentication + user-management endpoints (Gap 3, step 1). Cookie-based sessions. The bootstrap
/// endpoints (status/setup/login) are anonymous so an unconfigured instance can create its first admin
/// and users can sign in; everything else requires the fallback auth policy. Role-based restrictions on
/// the user-management routes come with the later RBAC step.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // Whether login is needed and whether the instance still needs its first admin.
        app.MapGet("/api/auth/status", async (HttpContext ctx, UserService users) =>
        {
            var setupRequired = await users.CountAsync() == 0;
            var authenticated = ctx.User.Identity?.IsAuthenticated == true;
            return Results.Ok(new
            {
                authenticated,
                username = authenticated ? ctx.User.Identity!.Name : null,
                userId = authenticated ? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value : null,
                setupRequired,
            });
        }).AllowAnonymous();

        // First-run: create the initial admin. Allowed only while zero users exist.
        app.MapPost("/api/auth/setup", async (LoginRequest request, HttpContext ctx, UserService users) =>
        {
            if (await users.CountAsync() > 0)
            {
                return Results.Conflict(new { message = "Setup has already been completed." });
            }
            try
            {
                var user = await users.CreateAsync(request.Username, request.Password, "admin");
                await SignInAsync(ctx, user);
                return Results.Ok(new { username = user.Username });
            }
            catch (System.ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        }).AllowAnonymous();

        app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext ctx, UserService users) =>
        {
            var user = await users.ValidateCredentialsAsync(request.Username, request.Password);
            if (user is null)
            {
                return Results.Json(new { message = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            await SignInAsync(ctx, user);
            return Results.Ok(new { username = user.Username });
        }).AllowAnonymous();

        app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { });
        });

        app.MapGet("/api/auth/users", async (UserService users) =>
        {
            var list = await users.ListAsync();
            return Results.Ok(list.Select(u => new { id = u.Id, username = u.Username, role = u.Role, createdAt = u.CreatedAt }));
        });

        app.MapPost("/api/auth/users", async (CreateUserRequest request, UserService users) =>
        {
            try
            {
                var user = await users.CreateAsync(request.Username, request.Password, string.IsNullOrWhiteSpace(request.Role) ? "user" : request.Role!);
                return Results.Ok(new { id = user.Id, username = user.Username, role = user.Role });
            }
            catch (System.ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (System.InvalidOperationException ex)
            {
                return Results.Conflict(new { message = ex.Message });
            }
        });

        app.MapDelete("/api/auth/users/{id}", async (string id, HttpContext ctx, UserService users) =>
        {
            // Guard against self-lockout mid-session.
            if (ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value == id)
            {
                return Results.BadRequest(new { message = "You cannot delete your own account while signed in." });
            }
            try
            {
                var deleted = await users.DeleteAsync(id);
                return deleted ? Results.Ok(new { deleted = true }) : Results.NotFound();
            }
            catch (System.InvalidOperationException ex)
            {
                return Results.Conflict(new { message = ex.Message });
            }
        });

        app.MapPost("/api/auth/change-password", async (ChangePasswordRequest request, HttpContext ctx, UserService users) =>
        {
            var id = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(id))
            {
                return Results.Unauthorized();
            }
            try
            {
                var ok = await users.ChangePasswordAsync(id, request.NewPassword);
                return ok ? Results.Ok(new { }) : Results.NotFound();
            }
            catch (System.ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });
    }

    private static async Task SignInAsync(HttpContext ctx, UserAccount user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    public sealed record LoginRequest(string Username, string Password);
    public sealed record CreateUserRequest(string Username, string Password, string? Role);
    public sealed record ChangePasswordRequest(string NewPassword);
}
