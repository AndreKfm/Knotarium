// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Knotarium.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Knotarium.Tests.Api;

/// <summary>
/// End-to-end coverage of the authentication layer with <c>Auth:Enabled=true</c>: first-run setup,
/// the gate on management endpoints, session cookie flow, logout, and that the machine-facing webhook
/// trigger stays anonymous. The default factory disables auth; this class enables it explicitly.
/// </summary>
public sealed class AuthEndpointTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _databasePath;

    public AuthEndpointTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"knotarium-auth-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Enabled", "true");
            builder.ConfigureServices(services =>
            {
                var db = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (db != null) services.Remove(db);
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
            });
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:Credentials:EncryptionKeyBase64"] = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=",
            }));
        });

        // The factory's default client handles cookies, so the session persists across requests.
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try { File.Delete(_databasePath); } catch { /* best-effort temp cleanup */ }
    }

    private sealed record StatusDto(bool Authenticated, string? Username, string? UserId, bool SetupRequired);

    [Fact]
    public async Task Setup_gates_and_logout_flow()
    {
        var initial = await _client.GetFromJsonAsync<StatusDto>("/api/auth/status");
        Assert.True(initial!.SetupRequired);
        Assert.False(initial.Authenticated);

        // Gated management endpoint rejected before any session exists.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/workflows")).StatusCode);

        // First-run setup creates the admin and signs in.
        var setup = await _client.PostAsJsonAsync("/api/auth/setup", new { username = "admin", password = "password123" });
        setup.EnsureSuccessStatusCode();

        var authed = await _client.GetFromJsonAsync<StatusDto>("/api/auth/status");
        Assert.True(authed!.Authenticated);
        Assert.False(authed.SetupRequired);
        Assert.Equal("admin", authed.Username);

        // Now the gated endpoint is reachable.
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/workflows")).StatusCode);

        // Setup cannot run twice.
        var secondSetup = await _client.PostAsJsonAsync("/api/auth/setup", new { username = "x", password = "password123" });
        Assert.Equal(HttpStatusCode.Conflict, secondSetup.StatusCode);

        // Logout drops the session; the gate rejects again.
        (await _client.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/workflows")).StatusCode);
    }

    [Fact]
    public async Task Login_rejects_wrong_password_and_accepts_correct()
    {
        (await _client.PostAsJsonAsync("/api/auth/setup", new { username = "admin", password = "password123" })).EnsureSuccessStatusCode();
        (await _client.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "nope" })).StatusCode);
        (await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "password123" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Webhook_trigger_endpoint_is_not_gated()
    {
        // The external trigger must stay reachable without a user session (machine-facing). With the
        // runtime disarmed it answers Conflict — the point is it is NOT 401.
        var response = await _client.PostAsJsonAsync("/api/executions", new { workflowDefinitionId = "does-not-exist" });
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_cannot_manage_users_or_self_promote()
    {
        // Bootstrap the admin and, as admin, create a low-privilege "user" account.
        (await _client.PostAsJsonAsync("/api/auth/setup", new { username = "admin", password = "password123" })).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync("/api/auth/users", new { username = "bob", password = "password123", role = "user" })).EnsureSuccessStatusCode();

        // Sign out the admin and sign in as the low-privilege user.
        (await _client.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync("/api/auth/login", new { username = "bob", password = "password123" })).EnsureSuccessStatusCode();

        // C1: the user-management routes are now admin-gated. A non-admin cannot list, cannot create an
        // admin for themselves (the privilege-escalation path), and cannot delete accounts.
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/auth/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.PostAsJsonAsync("/api/auth/users", new { username = "evil", password = "password123", role = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.DeleteAsync("/api/auth/users/anything")).StatusCode);

        // Privileged config mutations are likewise gated.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.PostAsJsonAsync("/api/runtime/arming", new { armed = true })).StatusCode);
    }

    [Fact]
    public async Task Admin_can_manage_users()
    {
        (await _client.PostAsJsonAsync("/api/auth/setup", new { username = "admin", password = "password123" })).EnsureSuccessStatusCode();

        var create = await _client.PostAsJsonAsync("/api/auth/users", new { username = "carol", password = "password123", role = "user" });
        create.EnsureSuccessStatusCode();

        var list = await _client.GetAsync("/api/auth/users");
        list.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Webhook_secret_gates_the_anonymous_trigger()
    {
        (await _client.PostAsJsonAsync("/api/auth/setup", new { username = "admin", password = "password123" })).EnsureSuccessStatusCode();

        // As admin, configure a webhook secret for a workflow id.
        const string workflowId = "wf-secret-1";
        (await _client.PutAsJsonAsync($"/api/workflows/{workflowId}/webhook-secret", new { secret = "s3cr3t-value" })).EnsureSuccessStatusCode();

        // Anonymous trigger without / with a wrong secret is rejected (401), regardless of runtime state.
        var missing = await _client.PostAsJsonAsync("/api/executions", new { workflowDefinitionId = workflowId });
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/executions")
        {
            Content = JsonContent.Create(new { workflowDefinitionId = workflowId }),
        };
        wrong.Headers.Add("X-Knotarium-Webhook-Secret", "not-it");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(wrong)).StatusCode);

        // With the correct secret the request passes the auth gate (then hits arming/existence checks → not 401).
        var right = new HttpRequestMessage(HttpMethod.Post, "/api/executions")
        {
            Content = JsonContent.Create(new { workflowDefinitionId = workflowId }),
        };
        right.Headers.Add("X-Knotarium-Webhook-Secret", "s3cr3t-value");
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await _client.SendAsync(right)).StatusCode);

        // A workflow with no secret configured stays open (not 401).
        var noSecret = await _client.PostAsJsonAsync("/api/executions", new { workflowDefinitionId = "wf-no-secret" });
        Assert.NotEqual(HttpStatusCode.Unauthorized, noSecret.StatusCode);
    }
}
