using KnotGarden.Features.Condition;
using KnotGarden.Features.Nodes.Condition;
using Xunit;

namespace KnotGarden.Tests.Nodes.Condition;

/// <summary>Schema-validation tests for the persisted logic blob (FIX list). Malformed → INVALID_LOGIC.</summary>
public class ConditionLogicParserTests
{
    private static bool Parse(string json, out ConditionLogic? logic, out ConditionError? error) =>
        ConditionLogicParser.TryParse(json, out logic, out error);

    [Fact]
    public void Parses_a_valid_binary_comparator()
    {
        var ok = Parse("""
            {"version":1,"comb":"and","cmps":[
              {"id":"c1","op":"eq",
               "a":{"kind":"lit","type":"number","value":5},
               "b":{"kind":"lit","type":"number","value":5}}]}
            """, out var logic, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(2, logic!.Version); // v1 normalized to a v2 tree
        // A single comparator wires straight through: the root IS the comparator (no wrapping group).
        var c = Assert.IsType<ComparatorNode>(logic.Root);
        Assert.Equal("eq", c.Op);
        Assert.Equal(OperandKind.Lit, c.A.Kind);
        Assert.Equal(5L, System.Convert.ToInt64(c.A.Value));
        Assert.NotNull(c.B);
    }

    [Fact]
    public void Parses_a_unary_comparator_without_b()
    {
        var ok = Parse("""
            {"version":1,"comb":"or","cmps":[
              {"id":"c1","op":"exists","a":{"kind":"ref","type":"string","ref":{"__type":"variable_ref","variableName":"x"}}}]}
            """, out var logic, out var error);

        Assert.True(ok);
        Assert.Null(error);
        var c = Assert.IsType<ComparatorNode>(logic!.Root);
        Assert.Null(c.B);
        Assert.Equal(OperandKind.Ref, c.A.Kind);
    }

    [Theory]
    [InlineData("""{"version":3,"comb":"and","cmps":[{"id":"c1","op":"eq","a":{"kind":"lit","type":"number","value":1},"b":{"kind":"lit","type":"number","value":1}}]}""", "version")]
    [InlineData("""{"version":1,"comb":"xor","cmps":[{"id":"c1","op":"eq","a":{"kind":"lit","type":"number","value":1},"b":{"kind":"lit","type":"number","value":1}}]}""", "comb")]
    [InlineData("""{"version":1,"comb":"and","cmps":[]}""", "at least one")]
    [InlineData("""{"version":1,"comb":"and","cmps":[{"id":"c1","op":"frobnicate","a":{"kind":"lit","type":"number","value":1},"b":{"kind":"lit","type":"number","value":1}}]}""", "unknown operator")]
    [InlineData("""{"version":1,"comb":"and","cmps":[{"id":"c1","op":"eq","a":{"kind":"lit","type":"number","value":1}}]}""", "requires operand 'b'")]
    [InlineData("""{"version":1,"comb":"and","cmps":[{"id":"c1","op":"eq","a":{"kind":"weird","type":"number","value":1},"b":{"kind":"lit","type":"number","value":1}}]}""", "kind must be")]
    [InlineData("""{"version":1,"comb":"and","cmps":[{"id":"c1","op":"eq","a":{"kind":"lit","type":"int","value":1},"b":{"kind":"lit","type":"number","value":1}}]}""", "type must be")]
    [InlineData("""{"version":1,"comb":"and","cmps":[{"id":"c1","op":"eq","a":{"kind":"lit","type":"number","value":"5"},"b":{"kind":"lit","type":"number","value":1}}]}""", "expected a number")]
    [InlineData("""{"version":1,"comb":"and","cmps":[{"id":"c1","op":"eq","a":{"kind":"ref","type":"number"},"b":{"kind":"lit","type":"number","value":1}}]}""", "missing 'ref'")]
    public void Rejects_malformed_logic(string json, string expectedMessageFragment)
    {
        var ok = Parse(json, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal(ConditionErrorCode.INVALID_LOGIC, error!.Code);
        Assert.Contains(expectedMessageFragment, error.Message);
    }

    [Fact]
    public void Rejects_duplicate_comparator_ids()
    {
        var ok = Parse("""
            {"version":1,"comb":"and","cmps":[
              {"id":"dup","op":"eq","a":{"kind":"lit","type":"number","value":1},"b":{"kind":"lit","type":"number","value":1}},
              {"id":"dup","op":"eq","a":{"kind":"lit","type":"number","value":2},"b":{"kind":"lit","type":"number","value":2}}]}
            """, out _, out var error);

        Assert.False(ok);
        Assert.Contains("duplicate", error!.Message);
    }

    [Fact]
    public void Rejects_too_many_comparators()
    {
        var items = new System.Text.StringBuilder();
        for (int i = 0; i <= ConditionLogicParser.MaxComparators; i++)
        {
            if (i > 0) items.Append(',');
            items.Append("{\"id\":\"c").Append(i).Append("\",\"op\":\"eq\",\"a\":{\"kind\":\"lit\",\"type\":\"number\",\"value\":1},\"b\":{\"kind\":\"lit\",\"type\":\"number\",\"value\":1}}");
        }
        var json = "{\"version\":1,\"comb\":\"and\",\"cmps\":[" + items + "]}";

        var ok = Parse(json, out _, out var error);

        Assert.False(ok);
        Assert.Contains("exceeds", error!.Message);
    }

    // ── v2 tree (Phase 8) ──────────────────────────────────────────────────

    private const string Leaf =
        """{"kind":"cmp","id":"%ID%","op":"eq","a":{"kind":"lit","type":"number","value":1},"b":{"kind":"lit","type":"number","value":1}}""";

    private static string LeafWith(string id) => Leaf.Replace("%ID%", id);

    [Fact]
    public void Parses_a_v2_tree_with_a_group_and_a_not()
    {
        var json =
            "{\"version\":2,\"root\":{\"kind\":\"group\",\"id\":\"g1\",\"op\":\"or\",\"children\":[" +
            LeafWith("c1") + ",{\"kind\":\"not\",\"id\":\"n1\",\"child\":" + LeafWith("c2") + "}]}}";

        Assert.True(Parse(json, out var logic, out var error));
        Assert.Null(error);
        var group = Assert.IsType<GroupNode>(logic!.Root);
        Assert.Equal(Combinator.Or, group.Op);
        Assert.Equal(2, group.Children.Count);
        Assert.IsType<ComparatorNode>(group.Children[0]);
        var not = Assert.IsType<NotNode>(group.Children[1]);
        Assert.IsType<ComparatorNode>(not.Child);
    }

    [Fact]
    public void Parses_a_v2_bare_comparator_root()
    {
        Assert.True(Parse("{\"version\":2,\"root\":" + LeafWith("only") + "}", out var logic, out _));
        Assert.IsType<ComparatorNode>(logic!.Root);
    }

    [Fact]
    public void Migrates_a_v1_multi_comparator_to_a_root_group()
    {
        var json = """
            {"version":1,"comb":"or","cmps":[
              {"id":"c1","op":"eq","a":{"kind":"lit","type":"number","value":1},"b":{"kind":"lit","type":"number","value":1}},
              {"id":"c2","op":"eq","a":{"kind":"lit","type":"number","value":2},"b":{"kind":"lit","type":"number","value":2}}]}
            """;

        Assert.True(Parse(json, out var logic, out _));
        Assert.Equal(2, logic!.Version);
        var group = Assert.IsType<GroupNode>(logic.Root);
        Assert.Equal(Combinator.Or, group.Op);
        Assert.Equal(2, group.Children.Count);
        Assert.All(group.Children, child => Assert.IsType<ComparatorNode>(child));
    }

    [Fact]
    public void Rejects_a_node_id_duplicated_anywhere_in_the_tree()
    {
        // The group id collides with a descendant comparator id — tree-unique ids (B10).
        var json = "{\"version\":2,\"root\":{\"kind\":\"group\",\"id\":\"x\",\"op\":\"and\",\"children\":[" + LeafWith("x") + "]}}";
        Assert.False(Parse(json, out _, out var error));
        Assert.Contains("duplicate", error!.Message);
    }

    [Theory]
    [InlineData("""{"version":2,"root":{"kind":"group","id":"g","op":"and","children":[]}}""", "at least one child")]
    [InlineData("""{"version":2,"root":{"kind":"not","id":"n"}}""", "requires a single 'child'")]
    [InlineData("""{"version":2,"root":{"kind":"frob","id":"g"}}""", "must be 'cmp', 'group', or 'not'")]
    [InlineData("""{"version":2}""", "root is required")]
    public void Rejects_malformed_v2_trees(string json, string fragment)
    {
        Assert.False(Parse(json, out _, out var error));
        Assert.Equal(ConditionErrorCode.INVALID_LOGIC, error!.Code);
        Assert.Contains(fragment, error.Message);
    }

    [Fact]
    public void Rejects_a_tree_deeper_than_the_max_depth()
    {
        // 25 nested nots around a leaf — the leaf sits well past MaxTreeDepth (20).
        string node = LeafWith("leaf");
        for (int i = 0; i < 25; i++) node = "{\"kind\":\"not\",\"id\":\"n" + i + "\",\"child\":" + node + "}";
        Assert.False(Parse("{\"version\":2,\"root\":" + node + "}", out _, out var error));
        Assert.Contains("depth", error!.Message);
    }

    [Fact]
    public void Rejects_a_group_with_too_many_children()
    {
        var children = new System.Text.StringBuilder();
        for (int i = 0; i <= ConditionLogicParser.MaxGroupChildren; i++)
        {
            if (i > 0) children.Append(',');
            children.Append(LeafWith("c" + i));
        }
        var json = "{\"version\":2,\"root\":{\"kind\":\"group\",\"id\":\"g\",\"op\":\"and\",\"children\":[" + children + "]}}";
        Assert.False(Parse(json, out _, out var error));
        Assert.Contains("children", error!.Message);
    }
}
