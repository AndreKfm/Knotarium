using System.Collections.Generic;
using System.Linq;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Ai;
using KnotGarden.Features.Compiler;
using Xunit;

namespace KnotGarden.Tests.Ai;

public class CatalogProjectionTests
{
    private static InMemoryNodePackageManifestProvider Provider() => new();

    private static NodePackageManifest PluginNode(string id, string category, string? description = null) =>
        new(new NodePackageId(id), "1.0.0", id, category,
            NodeTier.Declarative, NodeSideEffectKind.NonIdempotentSideEffect, RecoveryMode.FailImmediately,
            30, new List<string>(),
            new List<ParameterDefinition> { new("value", "string", false, true) },
            new List<OutputDefinition> { new("result") },
            description: description);

    [Fact]
    public void Project_IncludesPluginNodes_ButExcludesExternalDeviceById()
    {
        // Deployed plugin nodes carry a vendor category (not "External"); they should be surfaced, while
        // externalDevice stays excluded by id because its pins are dynamic (unwireable from the catalog).
        var manifests = new[]
        {
            PluginNode("fireAction", "External Device"),
            PluginNode("setEvent", "External Device"),
            PluginNode("externalDevice", "External Device"),
        };

        var ids = CatalogProjection.Project(manifests).Select(n => n.Id).ToHashSet();

        Assert.Contains("fireAction", ids);
        Assert.Contains("setEvent", ids);
        Assert.DoesNotContain("externalDevice", ids);
    }

    [Fact]
    public void Render_IncludesNodeDescription_WhenPresent()
    {
        var manifests = new[]
        {
            PluginNode("fireAction", "External Device", "Trigger a configured action on an external device."),
        };

        var rendered = CatalogProjection.Render(CatalogProjection.Project(manifests));

        // The description follows the category on the node header line — the key disambiguation signal.
        Assert.Contains("External Device: Trigger a configured action on an external device.", rendered);
    }

    [Fact]
    public void Project_KeepsEveryBuiltIn_ExceptExcludedCategories()
    {
        var manifests = Provider().GetAllManifests();
        var projected = CatalogProjection.Project(manifests);
        var ids = projected.Select(n => n.Id).ToHashSet();

        // A representative spread of categories survives.
        Assert.Contains("start", ids);
        Assert.Contains("httpRequest", ids);
        Assert.Contains("forLoop", ids);
        Assert.Contains("inlineCode", ids);

        // Annotations + External are dropped: inert / dynamically-pinned, not generatable.
        Assert.DoesNotContain("stickyNote", ids);
        Assert.DoesNotContain("group", ids);
        Assert.DoesNotContain("externalDevice", ids);

        // Nothing from an excluded category slips through.
        Assert.DoesNotContain(projected, n => n.Category is "Annotations" or "External");
    }

    [Fact]
    public void Project_PreservesRequiredFlagAndEnumValues()
    {
        var projected = CatalogProjection.Project(Provider().GetAllManifests());

        var forLoop = projected.Single(n => n.Id == "forLoop");
        var mode = forLoop.Parameters.Single(p => p.Name == "mode");

        Assert.True(mode.Required);
        Assert.Equal(new[] { "count", "foreach", "while", "batch" }, mode.Values);
    }

    [Fact]
    public void Project_MarksTriggerOnlyNodes()
    {
        var projected = CatalogProjection.Project(Provider().GetAllManifests());

        Assert.True(projected.Single(n => n.Id == "start").TriggerOnly);
        Assert.True(projected.Single(n => n.Id == "manualTrigger").TriggerOnly);
        Assert.False(projected.Single(n => n.Id == "httpRequest").TriggerOnly);
    }

    [Fact]
    public void Render_EmitsIdsTriggerMarkerEnumsAndPorts()
    {
        var rendered = CatalogProjection.ProjectAndRender(Provider().GetAllManifests());

        // Every kept id appears.
        Assert.Contains("httpRequest (HTTP Request)", rendered);
        // Trigger marker.
        Assert.Contains("[TRIGGER]", rendered);
        // Enum values inlined.
        Assert.Contains("mode:enum(count|foreach|while|batch)!", rendered);
        // Output ports rendered pipe-joined.
        Assert.Contains("outputs: success|error", rendered);
    }

    [Fact]
    public void Render_StaysWithinTokenBudget()
    {
        var rendered = CatalogProjection.ProjectAndRender(Provider().GetAllManifests());

        // The whole point of the inline-catalog decision: the full built-in catalog is small. A rough
        // chars/4 token estimate must stay well under the budget that justified inlining over retrieval.
        var approxTokens = rendered.Length / 4;
        Assert.True(approxTokens < 4000, $"Catalog projection ~{approxTokens} tokens — unexpectedly large.");
    }
}
