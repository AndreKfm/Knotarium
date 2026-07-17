// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Knotarium.Tests.Api;

/// <summary>
/// Shared host factory for the endpoint integration tests. Boots with authentication disabled
/// (<c>Auth:Enabled=false</c>) so the existing unauthenticated endpoint tests keep passing; the auth
/// layer itself is covered by <c>AuthEndpointTests</c>, which enables it explicitly.
/// </summary>
public sealed class KnotariumApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:Enabled", "false");
    }
}
