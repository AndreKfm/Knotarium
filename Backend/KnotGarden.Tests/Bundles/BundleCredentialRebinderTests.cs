using System;
using System.Collections.Generic;
using System.Text.Json;
using KnotGarden.Api.Services;
using KnotGarden.Features.Execution;
using KnotGarden.Features.Portability;
using KnotGarden.Features.Bundles;
using KnotGarden.Core.Domain;
using Xunit;

namespace KnotGarden.Tests.Bundles;

public class BundleCredentialRebinderTests
{
    private static WorkflowExportDocument DocumentWith(params NodeDefinition[] nodes)
    {
        var manifest = new WorkflowExportManifest("wf-1", "WF", 1, "Imported", null, "stale-checksum");
        return new WorkflowExportDocument(manifest, new WorkflowExportContent(nodes, Array.Empty<EdgeDefinition>()));
    }

    private static NodeDefinition Node(string id, params (string Key, object Value)[] properties)
    {
        var dict = new Dictionary<string, object>();
        foreach (var (key, value) in properties)
        {
            dict[key] = value;
        }

        return new NodeDefinition(NodeId.Create(id), "http", dict);
    }

    // Parses a JSON literal into a JsonElement, mimicking how deserialized node properties arrive.
    private static JsonElement Json(string literal) => JsonDocument.Parse(literal).RootElement.Clone();

    [Fact]
    public void Rebind_PlainStringSlot_ReplacedWithCredentialId()
    {
        var doc = DocumentWith(Node("n1", ("apiKeySecretRef", "slot:smtp")));
        var bindings = new Dictionary<string, string> { ["smtp"] = "cred-123" };

        var result = BundleCredentialRebinder.Rebind(doc, bindings);

        Assert.Equal("cred-123", result.Document.Content.Nodes[0].Properties["apiKeySecretRef"]);
        Assert.Equal(new[] { "smtp" }, result.ReboundSlots);
        Assert.Empty(result.UnboundSlots);
        // Checksum is recomputed off the rewritten content, not left stale.
        Assert.NotEqual("stale-checksum", result.Document.Manifest.Checksum);
    }

    [Fact]
    public void Rebind_JsonElementStringSlot_Replaced()
    {
        var doc = DocumentWith(Node("n1", ("apiKeySecretRef", Json("\"slot:smtp\""))));
        var bindings = new Dictionary<string, string> { ["smtp"] = "cred-123" };

        var result = BundleCredentialRebinder.Rebind(doc, bindings);

        Assert.Equal("cred-123", result.Document.Content.Nodes[0].Properties["apiKeySecretRef"]);
        Assert.Equal(new[] { "smtp" }, result.ReboundSlots);
    }

    [Fact]
    public void Rebind_UnboundSlot_LeftInPlaceAndReported()
    {
        var doc = DocumentWith(Node("n1", ("apiKeySecretRef", "slot:smtp")));

        var result = BundleCredentialRebinder.Rebind(doc, new Dictionary<string, string>());

        Assert.Equal("slot:smtp", result.Document.Content.Nodes[0].Properties["apiKeySecretRef"]);
        Assert.Equal(new[] { "smtp" }, result.UnboundSlots);
        Assert.Empty(result.ReboundSlots);
        // No change ⇒ original document returned untouched (checksum not disturbed).
        Assert.Same(doc, result.Document);
    }

    [Fact]
    public void Rebind_NonSlotValues_Untouched()
    {
        var doc = DocumentWith(Node("n1",
            ("url", "https://example.com"),
            ("count", Json("42")),
            ("enabled", Json("true"))));

        var result = BundleCredentialRebinder.Rebind(doc, new Dictionary<string, string> { ["smtp"] = "cred-123" });

        Assert.Same(doc, result.Document);
        Assert.Empty(result.ReboundSlots);
        Assert.Empty(result.UnboundSlots);
    }

    [Fact]
    public void Rebind_SlotNestedInJsonObjectAndArray_RewrittenDeeply()
    {
        var doc = DocumentWith(Node("n1",
            ("auth", Json("{\"primary\":\"slot:smtp\",\"other\":\"keep\"}")),
            ("fallbacks", Json("[\"slot:backup\",\"literal\"]"))));
        var bindings = new Dictionary<string, string> { ["smtp"] = "cred-1", ["backup"] = "cred-2" };

        var result = BundleCredentialRebinder.Rebind(doc, bindings);

        var auth = Assert.IsType<Dictionary<string, object>>(result.Document.Content.Nodes[0].Properties["auth"]);
        Assert.Equal("cred-1", auth["primary"]);
        Assert.Equal("keep", auth["other"]);

        var fallbacks = Assert.IsType<List<object?>>(result.Document.Content.Nodes[0].Properties["fallbacks"]);
        Assert.Equal("cred-2", fallbacks[0]);
        Assert.Equal("literal", fallbacks[1]);

        Assert.Equal(new HashSet<string> { "smtp", "backup" }, new HashSet<string>(result.ReboundSlots));
    }

    [Fact]
    public void Rebind_OnlyBoundSlotsRewritten_WhenMixed()
    {
        var doc = DocumentWith(
            Node("n1", ("ref", "slot:bound")),
            Node("n2", ("ref", "slot:unbound")));

        var result = BundleCredentialRebinder.Rebind(doc, new Dictionary<string, string> { ["bound"] = "cred-1" });

        Assert.Equal("cred-1", result.Document.Content.Nodes[0].Properties["ref"]);
        Assert.Equal("slot:unbound", result.Document.Content.Nodes[1].Properties["ref"]);
        Assert.Equal(new[] { "bound" }, result.ReboundSlots);
        Assert.Equal(new[] { "unbound" }, result.UnboundSlots);
    }
}
