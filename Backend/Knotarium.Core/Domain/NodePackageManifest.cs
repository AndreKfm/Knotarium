using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knotarium.Core.Domain;

public sealed record ParameterDefinition(
    string Name,
    string Type,
    bool Required,
    bool Expression,
    List<string>? Values = null,
    // Dynamic-options / resource-locator support. All optional and additive so existing manifests
    // keep deserializing. When Type is "dynamicOptions"/"resourceLocator", OptionsLoader names the
    // server-side loader (the design-time allowlist key) and IntegrationType is the endpoint
    // namespace. DependsOn lists sibling parameter names whose values are passed to the loader;
    // LoaderConfig carries static loader configuration (e.g. path/labelField). Multiple persists an
    // order-preserving array of stable keys; AllowManualEntry permits a free-text fallback when the
    // system is unreachable at design time.
    string? OptionsLoader = null,
    string? IntegrationType = null,
    List<string>? DependsOn = null,
    Dictionary<string, string>? LoaderConfig = null,
    bool AllowManualEntry = false,
    bool Multiple = false,
    // Optional human-facing hint rendered under the field in the properties form.
    // Additive/optional so existing manifests keep deserializing.
    string? Description = null
);

/// <summary>A named field within a structured (object) output or input payload.</summary>
public sealed record FieldSchema(
    string Name,
    string Type = "any",
    bool Required = false
);

public sealed record OutputDefinition(
    string Name,
    // Declared value type of this output socket. Defaults to "any" so existing manifests
    // (and untyped user packages) keep compiling and never trigger type-mismatch warnings
    // until they opt in by declaring a concrete type.
    string Type = "any",
    // Optional field-level schema (Phase B). When set, the compiler can verify that downstream
    // consumers' required fields are actually delivered. Null = unstructured (no field checks).
    List<FieldSchema>? Fields = null
);

/// <summary>
/// A data input socket a node consumes from upstream (distinct from configuration
/// <see cref="ParameterDefinition"/>). Declaring required <see cref="Fields"/> opts the node
/// into field-level compile-time checking against the producing output's schema.
/// </summary>
public sealed record InputDefinition(
    string Name,
    string Type = "any",
    List<FieldSchema>? Fields = null
);

public sealed record RetryPolicy(
    int MaxAttempts = 3,
    int InitialDelaySeconds = 2,
    double BackoffRate = 2.0,
    bool Jitter = true,
    int MaxDelaySeconds = 30
);

public sealed record NodePackageManifest
{
    public NodePackageId Id { get; init; }
    public string Version { get; init; }
    public string DisplayName { get; init; }
    public string Category { get; init; }
    public NodeTier Tier { get; init; }
    public NodeSideEffectKind? SideEffectKind { get; init; }
    public RecoveryMode RecoveryMode { get; init; }
    public int DefaultTimeoutSeconds { get; init; }
    public List<string> Capabilities { get; init; }
    public List<ParameterDefinition> Parameters { get; init; }
    public List<OutputDefinition> Outputs { get; init; }
    /// <summary>Typed data inputs this node consumes from upstream (Phase B). Empty = none declared.</summary>
    public List<InputDefinition> Inputs { get; init; }
    public RetryPolicy? RetryPolicy { get; init; }
    public bool TriggerOnly { get; init; }
    /// <summary>
    /// Editor hint: a de-emphasized "escape hatch" node. The palette routes these into a collapsed
    /// "Advanced" section so a primary block is the obvious choice. Additive/optional — defaults false.
    /// </summary>
    public bool Secondary { get; init; }

    /// <summary>
    /// Optional one-line summary of what the node does. Surfaced to AI workflow generation so the model
    /// can map a natural-language intent to the right node (display name + category alone are ambiguous for
    /// vendor packs). Additive/optional — defaults null; unset nodes are simply described by name+category.
    /// </summary>
    public string? Description { get; init; }

    [JsonConstructor]
    public NodePackageManifest(
        NodePackageId id,
        string version,
        string displayName,
        string category,
        NodeTier tier,
        NodeSideEffectKind? sideEffectKind,
        RecoveryMode recoveryMode,
        int defaultTimeoutSeconds,
        List<string> capabilities,
        List<ParameterDefinition> parameters,
        List<OutputDefinition> outputs,
        RetryPolicy? retryPolicy = null,
        bool triggerOnly = false,
        List<InputDefinition>? inputs = null,
        bool secondary = false,
        string? description = null)
    {
        Id = id;
        Version = version;
        DisplayName = displayName;
        Category = category;
        Tier = tier;
        SideEffectKind = sideEffectKind ?? NodeSideEffectKind.NonIdempotentSideEffect;
        RecoveryMode = recoveryMode;
        DefaultTimeoutSeconds = defaultTimeoutSeconds;
        Capabilities = capabilities ?? new List<string>();
        Parameters = parameters ?? new List<ParameterDefinition>();
        Outputs = outputs ?? new List<OutputDefinition>();
        Inputs = inputs ?? new List<InputDefinition>();
        RetryPolicy = retryPolicy ?? new RetryPolicy();
        TriggerOnly = triggerOnly;
        Secondary = secondary;
        Description = description;
    }
}
