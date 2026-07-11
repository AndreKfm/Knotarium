using System.Collections.Generic;

namespace KnotGarden.Infrastructure.Security;

public sealed class HttpEgressPolicyOptions
{
    public const string SectionName = "Security:HttpEgress";

    public List<string> AllowDomains { get; init; } = new();
    public List<string> BlockDomains { get; init; } = new();
    public bool DenyPrivateNetworks { get; init; } = true;
}