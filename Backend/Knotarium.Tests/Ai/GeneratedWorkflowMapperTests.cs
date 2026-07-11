using System.Text.Json;
using Knotarium.Features.Ai;
using Xunit;

namespace Knotarium.Tests.Ai;

/// <summary>
/// The flat-JSON ↔ domain bridge. A structured property (a resourceLocator {value,label,mode} pick, a
/// keyValue array, a condition logic graph) must survive the round-trip as structured JSON — NOT be
/// flattened to a string, which corrupted editor list-picks into "manually entered" text and broke
/// modify-in-place (ToFlatJson shows the model the real object; the echoed object must re-parse as one).
/// </summary>
public class GeneratedWorkflowMapperTests
{
    [Fact]
    public void TryParse_preserves_a_resourceLocator_object_pick_as_structured_json()
    {
        const string raw = """
        { "name": "w", "nodes": [
          { "id": "f", "type": "fireAction", "properties": {
              "instance": { "value": "site-a", "label": "Site A (main site)", "mode": "list" },
              "action":   { "value": "CustomAction", "label": "Custom Action", "mode": "list" },
              "globalCameraNumber": 1
          } } ] }
        """;

        var (workflow, error) = GeneratedWorkflowMapper.TryParse(raw);

        Assert.Null(error);
        var props = workflow!.Nodes[0].Properties;

        var action = Assert.IsType<JsonElement>(props["action"]);
        Assert.Equal(JsonValueKind.Object, action.ValueKind);
        Assert.Equal("CustomAction", action.GetProperty("value").GetString());
        Assert.Equal("Custom Action", action.GetProperty("label").GetString());
        Assert.Equal("list", action.GetProperty("mode").GetString());

        var instance = Assert.IsType<JsonElement>(props["instance"]);
        Assert.Equal("site-a", instance.GetProperty("value").GetString());

        // Scalars still unbox to their CLR primitive.
        Assert.Equal(1L, props["globalCameraNumber"]);
    }

    [Fact]
    public void TryParse_preserves_an_array_property_as_structured_json()
    {
        const string raw = """
        { "name": "w", "nodes": [
          { "id": "h", "type": "httpRequest", "properties": {
              "headers": [ { "name": "X-Api", "value": "{{ $node.a.output.k }}" } ]
          } } ] }
        """;

        var (workflow, error) = GeneratedWorkflowMapper.TryParse(raw);

        Assert.Null(error);
        var headers = Assert.IsType<JsonElement>(workflow!.Nodes[0].Properties["headers"]);
        Assert.Equal(JsonValueKind.Array, headers.ValueKind);
        Assert.Equal("X-Api", headers[0].GetProperty("name").GetString());
    }

    [Fact]
    public void ToFlatJson_then_TryParse_round_trips_a_structured_pick_unchanged()
    {
        const string raw = """
        { "name": "w", "nodes": [
          { "id": "f", "type": "fireAction", "properties": {
              "action": { "value": "CustomAction", "label": "Custom Action", "mode": "list" }
          } } ], "edges": [] }
        """;

        var (first, _) = GeneratedWorkflowMapper.TryParse(raw);
        var flat = GeneratedWorkflowMapper.ToFlatJson(first!);
        var (second, error) = GeneratedWorkflowMapper.TryParse(flat);

        Assert.Null(error);
        var action = Assert.IsType<JsonElement>(second!.Nodes[0].Properties["action"]);
        Assert.Equal(JsonValueKind.Object, action.ValueKind);
        Assert.Equal("Custom Action", action.GetProperty("label").GetString());
    }
}
