using System;
using System.Collections.Generic;
using System.Linq;
using Knotarium.Core.Domain;

namespace Knotarium.Features.NodeEditor;

/// <summary>
/// YAML document shape of a node-editor draft manifest, plus its mapping to the
/// domain <see cref="NodePackageManifest"/>.
/// </summary>
internal sealed class ManifestDocument
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = "Utility";
    public string Tier { get; set; } = nameof(NodeTier.Compiled);
    public string SideEffectKindName { get; set; } = nameof(NodeSideEffectKind.IdempotentSideEffect);
    public string RecoveryModeName { get; set; } = nameof(RecoveryMode.FailImmediately);
    public int DefaultTimeoutSeconds { get; set; }
    public List<string> Capabilities { get; set; } = new();
    public List<ParameterDocument> Parameters { get; set; } = new();
    public List<OutputDocument> Outputs { get; set; } = new();

    public NodeTier GetTier()
    {
        return ParseEnumOrDefault(Tier, NodeTier.Compiled);
    }

    public NodePackageManifest ToDomainManifest(string fallbackPackageId)
    {
        var packageId = string.IsNullOrWhiteSpace(Id) ? fallbackPackageId : Id;
        var displayName = string.IsNullOrWhiteSpace(DisplayName) ? packageId : DisplayName;

        return new NodePackageManifest(
            new NodePackageId(packageId),
            string.IsNullOrWhiteSpace(Version) ? "1.0.0" : Version,
            displayName,
            string.IsNullOrWhiteSpace(Category) ? "Utility" : Category,
            GetTier(),
            ParseEnumOrDefault(SideEffectKindName, NodeSideEffectKind.IdempotentSideEffect),
            ParseEnumOrDefault(RecoveryModeName, Knotarium.Core.Domain.RecoveryMode.FailImmediately),
            DefaultTimeoutSeconds,
            Capabilities ?? new List<string>(),
            Parameters?.Select(p => new ParameterDefinition(p.Name, p.Type, p.Required, p.Expression, p.Values)).ToList() ?? new List<ParameterDefinition>(),
            Outputs?.Where(o => !string.IsNullOrWhiteSpace(o.Name)).Select(o => new OutputDefinition(o.Name)).ToList() ?? new List<OutputDefinition>()
        );
    }

    private static TEnum ParseEnumOrDefault<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, true, out var parsed) ? parsed : fallback;
    }
}

internal sealed class ParameterDocument
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public bool Expression { get; set; }
    public List<string>? Values { get; set; }
}

internal sealed class OutputDocument
{
    public string Name { get; set; } = string.Empty;
}

internal sealed class TestsDocument
{
    public List<TestCaseDocument> Cases { get; set; } = new();
}

internal sealed class TestCaseDocument
{
    public string Name { get; set; } = "Unnamed test";
    public Dictionary<string, object?> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ExpectedOutput { get; set; } = "success";
}
