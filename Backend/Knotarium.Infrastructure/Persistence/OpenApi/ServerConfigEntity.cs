// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Knotarium.Infrastructure.Persistence.OpenApi;

public class ServerConfigEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ServerVariablesJson { get; set; } = "{}";
    public string SecuritySchemeType { get; set; } = string.Empty;
    public string? CredentialRef { get; set; }
    public bool AllowInsecureCertificate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
