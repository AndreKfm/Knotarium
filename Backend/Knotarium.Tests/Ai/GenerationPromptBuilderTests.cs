using System;
using System.Collections.Generic;
using Knotarium.Core.Domain;
using Knotarium.Features.Ai;
using Knotarium.Features.Compiler;
using Xunit;

namespace Knotarium.Tests.Ai;

public class GenerationPromptBuilderTests
{
    private static InMemoryNodePackageManifestProvider Provider() => new();

    private static WorkflowDefinition SampleWorkflow() =>
        new(WorkflowDefinitionId.New(), "Ping",
            new[]
            {
                new NodeDefinition(NodeId.Create("t"), "manualTrigger", new Dictionary<string, object>()),
                new NodeDefinition(NodeId.Create("h"), "httpRequest", new Dictionary<string, object> { ["url"] = "https://example.com" }),
            },
            new[] { new EdgeDefinition("e1", NodeId.Create("t"), "result", NodeId.Create("h"), "in") });

    [Fact]
    public void SystemPrompt_EmbedsCatalogRulesAndSlotConvention()
    {
        var prompt = GenerationPromptBuilder.BuildSystemPrompt(Provider().GetAllManifests());

        // Catalog is inlined.
        Assert.Contains("httpRequest (HTTP Request)", prompt);
        // The slot: credential convention is spelled out (reuses the existing portability primitive).
        Assert.Contains("slot:<kebab-case-name>", prompt);
        // The no-coordinates rule is stated (geometry is assigned by auto-layout).
        Assert.Contains("Do NOT include node positions", prompt);
        // The flat output contract is described.
        Assert.Contains("\"nodes\":", prompt);
        Assert.Contains("\"edges\":", prompt);
    }

    [Fact]
    public void UserMessage_PlainIntent_HasNoRepairPreamble()
    {
        var msg = GenerationPromptBuilder.BuildUserMessage("Ping a webhook every morning");

        Assert.Contains("Ping a webhook every morning", msg);
        Assert.DoesNotContain("failed validation", msg);
    }

    [Fact]
    public void UserMessage_WithPriorErrors_ThreadsThemBackForRepair()
    {
        var msg = GenerationPromptBuilder.BuildUserMessage(
            "Ping a webhook every morning",
            new[] { "ERR_INVALID_NODE_TYPE: 'htpRequest' is not a known node type" });

        Assert.Contains("failed validation", msg);
        Assert.Contains("ERR_INVALID_NODE_TYPE", msg);
    }

    [Fact]
    public void UserMessage_WithCurrentWorkflow_SwitchesToRefineMode()
    {
        var msg = GenerationPromptBuilder.BuildUserMessage(
            "Add a log node after the HTTP request", priorErrors: null, currentWorkflow: SampleWorkflow());

        // Refine mode: the existing workflow is embedded and the change is framed as a modification.
        Assert.Contains("MODIFYING an existing workflow", msg);
        Assert.Contains("\"httpRequest\"", msg);           // current workflow serialized in flat shape
        Assert.Contains("Change to apply:", msg);
        Assert.Contains("Add a log node after the HTTP request", msg);
    }

    [Fact]
    public void ToFlatJson_RoundTripsThroughTheParser()
    {
        var flat = GeneratedWorkflowMapper.ToFlatJson(SampleWorkflow());
        var (workflow, error) = GeneratedWorkflowMapper.TryParse(flat);

        Assert.Null(error);
        Assert.NotNull(workflow);
        Assert.Equal(2, workflow!.Nodes.Count);
        Assert.Single(workflow.Edges);
        Assert.Equal("httpRequest", workflow.Nodes[1].Type);
        Assert.Equal("https://example.com", workflow.Nodes[1].Properties["url"]);
    }
}
