using System;
using System.Collections.Generic;

namespace Knotarium.Core.Domain.OpenApi;

public sealed record ServerConfigInfo(
    string Id,
    string Name,
    string BaseUrl,
    IReadOnlyDictionary<string, string> ServerVariables,
    string SecuritySchemeType,
    string? CredentialRef,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    // Skip TLS certificate validation for calls to this server (self-signed / untrusted certs,
    // e.g. a dev/LAN appliance). Off by default; the egress policy still applies.
    bool AllowInsecureCertificate = false
);
